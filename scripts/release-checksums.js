#!/usr/bin/env node
/**
 * Appends or updates a "Checksums and scans" section in a GitHub release's notes:
 * computes the SHA-256 for every asset and links VirusTotal scan reports for
 * executable installer binaries.
 *
 * Feed metadata (assets.*.json, *.nupkg, optimum-manifest-*.json, .blockmap)
 * are listed by hash only and not submitted to VirusTotal.
 *
 * VirusTotal API calls are best effort:
 * - Assets are queried by hash first (GET /files/<sha256>).
 * - If missing and key is present, uploads for analysis.
 * - Missing API key, rate limits, timeouts, or service outages downgrade the
 *   VirusTotal column to a direct link by hash without blocking the release.
 *
 * Manual run:
 *   GH_TOKEN=... VIRUSTOTAL_API_KEY=... node scripts/release-checksums.js v0.3.21
 */

const { createHash } = require("node:crypto")
const { createReadStream, openAsBlob, readdirSync, statSync, writeFileSync, mkdtempSync, rmSync } = require("node:fs")
const { tmpdir } = require("node:os")
const { join } = require("node:path")
const { spawnSync } = require("node:child_process")

const SECTION_HEADING = "## Checksums and scans"

// VirusTotal accepts direct POST uploads up to 32 MB. Files exceeding 32 MB
// require an upload URL via GET /files/upload_url.
const DIRECT_UPLOAD_LIMIT = 32 * 1024 * 1024

// Public API rate limit: 4 requests per minute. Enforce a 15-second gap.
const QUOTA_INTERVAL_MS = 15_000

// Maximum time to wait for a submitted analysis before falling back to a report link.
const ANALYSIS_BUDGET_MS = 10 * 60 * 1000

// Update metadata and feeds are listed by hash only; engines report unsupported file type.
const METADATA_PATTERNS = [
  /\.json$/i,
  /\.nupkg$/i,
  /\.blockmap$/i,
  /\.ya?ml$/i,
  /^RELEASES$/i
]

const NOTE =
  "Feed files (`assets.*.json`, `*.nupkg`) and metadata are listed by hash only and not submitted for executable analysis. To verify a download: `certutil -hashfile <file> SHA256` on Windows, `sha256sum <file>` on Linux, or `shasum -a 256 <file>` on macOS. VirusTotal results are advisory and provide external engine scan reports; they are not a guarantee of safety or a substitute for code signing."

// Safe tag pattern: letters, numbers, '.', '_', '-'
const TAG_PATTERN = /^[A-Za-z0-9._-]+$/

function validateTag(tag) {
  if (!TAG_PATTERN.test(tag)) {
    throw new Error(`refusing tag ${JSON.stringify(tag)}: expected only letters, digits, '.', '_', '-'`)
  }
  return tag
}

function needsUploadUrl(sizeBytes) {
  return sizeBytes > DIRECT_UPLOAD_LIMIT
}

function quotaDelayMs(lastRequestAt, now) {
  if (lastRequestAt === undefined) return 0
  return Math.max(0, QUOTA_INTERVAL_MS - (now - lastRequestAt))
}

function isMetadata(name) {
  return METADATA_PATTERNS.some((pattern) => pattern.test(name))
}

function formatRow({ name, sha256, scan }) {
  const link = `https://www.virustotal.com/gui/file/${sha256}`
  let cell = "not scanned"
  if (scan !== undefined) {
    cell = scan.engines === undefined ? `[report](${link})` : `[${scan.detected} of ${scan.engines} engines](${link})`
  }
  return `| ${name} | \`${sha256}\` | ${cell} |`
}

function buildSection(rows) {
  return [SECTION_HEADING, "", "| File | SHA-256 | VirusTotal |", "| --- | --- | --- |", ...rows.map(formatRow), "", NOTE].join("\n")
}

function withSection(body, section) {
  const lines = (body ?? "").split("\n")
  const start = lines.findIndex((line) => line.trim() === SECTION_HEADING)
  if (start === -1) {
    const head = (body ?? "").trimEnd()
    return head === "" ? `${section}\n` : `${head}\n\n${section}\n`
  }

  let end = lines.length
  for (let index = start + 1; index < lines.length; index++) {
    if (lines[index].startsWith("## ")) {
      end = index
      break
    }
  }
  const before = lines.slice(0, start).join("\n").trimEnd()
  const after = lines.slice(end).join("\n").trim()
  const head = before === "" ? section : `${before}\n\n${section}`
  return after === "" ? `${head}\n` : `${head}\n\n${after}\n`
}

function warn(message) {
  console.warn(`[release-checksums] ${message}`)
}

function gh(args, { capture = true } = {}) {
  const result = spawnSync("gh", args, { encoding: "utf8", stdio: capture ? ["ignore", "pipe", "inherit"] : "inherit" })
  if (result.status !== 0) throw new Error(`gh ${args[0]} ${args[1]} failed with status ${result.status}`)
  return result.stdout ?? ""
}

function sha256Of(path) {
  return new Promise((resolve, reject) => {
    const hash = createHash("sha256")
    createReadStream(path)
      .on("error", reject)
      .on("data", (chunk) => hash.update(chunk))
      .on("end", () => resolve(hash.digest("hex")))
  })
}

const sleep = (ms) => new Promise((resolve) => setTimeout(resolve, ms))

async function virusTotal(url, init = {}) {
  await sleep(quotaDelayMs(virusTotal.lastRequestAt, Date.now()))
  virusTotal.lastRequestAt = Date.now()

  const response = await fetch(url, {
    ...init,
    headers: { "x-apikey": process.env.VIRUSTOTAL_API_KEY, ...init.headers },
    signal: AbortSignal.timeout(init.timeoutMs ?? 10 * 60 * 1000)
  })
  if (!response.ok && response.status !== 404) {
    throw new Error(`${init.method ?? "GET"} ${new URL(url).pathname} returned ${response.status}`)
  }
  return response
}

function countedEngines(stats) {
  const detected = (stats.malicious ?? 0) + (stats.suspicious ?? 0)
  const engines = detected + (stats.undetected ?? 0) + (stats.harmless ?? 0) + (stats.timeout ?? 0)
  return { detected, engines }
}

async function submit(path, name, sizeBytes) {
  let endpoint = "https://www.virustotal.com/api/v3/files"
  if (needsUploadUrl(sizeBytes)) {
    const urlResponse = await virusTotal("https://www.virustotal.com/api/v3/files/upload_url")
    endpoint = (await urlResponse.json()).data
  }

  const form = new FormData()
  form.append("file", await openAsBlob(path), name)
  const response = await virusTotal(endpoint, { method: "POST", body: form })
  return (await response.json()).data.id
}

async function waitForAnalysis(analysisId) {
  const deadline = Date.now() + ANALYSIS_BUDGET_MS
  while (Date.now() < deadline) {
    const response = await virusTotal(`https://www.virustotal.com/api/v3/analyses/${analysisId}`)
    const attributes = (await response.json()).data.attributes
    if (attributes.status === "completed") return countedEngines(attributes.stats)
  }
  warn("analysis did not complete inside the poll budget, linking the file page instead")
  return {}
}

async function scan(path, name, sha256) {
  if (!process.env.VIRUSTOTAL_API_KEY) return {}

  try {
    const known = await virusTotal(`https://www.virustotal.com/api/v3/files/${sha256}`)
    if (known.ok) {
      const stats = (await known.json()).data.attributes.last_analysis_stats
      console.log(`[release-checksums] ${name}: already known to VirusTotal`)
      return countedEngines(stats)
    }

    const sizeBytes = statSync(path).size
    console.log(`[release-checksums] ${name}: uploading ${(sizeBytes / 1024 / 1024).toFixed(1)} MB to VirusTotal`)
    return await waitForAnalysis(await submit(path, name, sizeBytes))
  } catch (error) {
    warn(`VirusTotal is unavailable for ${name} (${error.message}), linking the file page instead`)
    return {}
  }
}

async function main() {
  const tag = process.argv[2]
  if (!tag) {
    console.error("usage: node scripts/release-checksums.js <tag>")
    process.exit(2)
  }
  try {
    validateTag(tag)
  } catch (error) {
    console.error(`[release-checksums] ${error.message}`)
    process.exit(2)
  }

  const workdir = mkdtempSync(join(tmpdir(), "release-checksums-"))
  try {
    gh(["release", "download", tag, "--dir", workdir, "--clobber"], { capture: false })

    const rows = []
    for (const name of readdirSync(workdir).sort()) {
      const path = join(workdir, name)
      const sha256 = await sha256Of(path)
      rows.push({ name, sha256, scan: isMetadata(name) ? undefined : await scan(path, name, sha256) })
    }

    const body = JSON.parse(gh(["release", "view", tag, "--json", "body"])).body
    const notes = join(workdir, "notes.md")
    writeFileSync(notes, withSection(body, buildSection(rows)))
    gh(["release", "edit", tag, "--notes-file", notes], { capture: false })
    console.log(`[release-checksums] wrote ${rows.length} rows to the notes of ${tag}`)
  } finally {
    rmSync(workdir, { recursive: true, force: true })
  }
}

module.exports = {
  SECTION_HEADING,
  DIRECT_UPLOAD_LIMIT,
  QUOTA_INTERVAL_MS,
  METADATA_PATTERNS,
  TAG_PATTERN,
  validateTag,
  needsUploadUrl,
  quotaDelayMs,
  isMetadata,
  formatRow,
  buildSection,
  withSection,
  sha256Of,
  countedEngines
}

if (require.main === module) {
  main().catch((error) => {
    console.error(`[release-checksums] ${error.message}`)
    process.exit(1)
  })
}
