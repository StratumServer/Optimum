const test = require("node:test")
const assert = require("node:assert/strict")
const { writeFileSync, mkdtempSync, rmSync } = require("node:fs")
const { tmpdir } = require("node:os")
const { join } = require("node:path")
const {
  SECTION_HEADING,
  DIRECT_UPLOAD_LIMIT,
  QUOTA_INTERVAL_MS,
  validateTag,
  needsUploadUrl,
  quotaDelayMs,
  isMetadata,
  formatRow,
  buildSection,
  withSection,
  sha256Of,
  countedEngines
} = require("../release-checksums.js")

test("validateTag accepts safe tags and rejects unsafe input", () => {
  assert.equal(validateTag("v0.3.21"), "v0.3.21")
  assert.equal(validateTag("v1.0.0-rc1"), "v1.0.0-rc1")
  assert.equal(validateTag("installer-v0.2.0"), "installer-v0.2.0")
  assert.equal(validateTag("v0.1.0_build.1"), "v0.1.0_build.1")

  assert.throws(() => validateTag("; rm -rf /"), /refusing tag/)
  assert.throws(() => validateTag("v1.0$(whoami)"), /refusing tag/)
  assert.throws(() => validateTag("v1.0`id`"), /refusing tag/)
  assert.throws(() => validateTag("tag with space"), /refusing tag/)
  assert.throws(() => validateTag("tag/with/slash"), /refusing tag/)
  assert.throws(() => validateTag(""), /refusing tag/)
})

test("isMetadata classifies feed and metadata files vs installer binaries", () => {
  // Feed metadata
  assert.equal(isMetadata("assets.win.json"), true)
  assert.equal(isMetadata("assets.linux.json"), true)
  assert.equal(isMetadata("assets.osx.json"), true)
  assert.equal(isMetadata("Optimum.Installer-0.1.0-full.nupkg"), true)
  assert.equal(isMetadata("Optimum.Installer-0.1.0-delta.nupkg"), true)
  assert.equal(isMetadata("Optimum-v0.3.21-Setup.exe.blockmap"), true)
  assert.equal(isMetadata("RELEASES"), true)
  assert.equal(isMetadata("releases"), true)
  assert.equal(isMetadata("optimum-manifest-linux-x64.json"), true)
  assert.equal(isMetadata("config.yml"), true)
  assert.equal(isMetadata("manifest.yaml"), true)

  // Executable binaries and archives
  assert.equal(isMetadata("Optimum-v0.3.21-win-x64-Setup.exe"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-win-x64-Installer-Portable.zip"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-linux-x64-Installer.AppImage"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-linux-x64-Installer.deb"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-linux-x64-Installer.rpm"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-osx-arm64-Installer.pkg"), false)
  assert.equal(isMetadata("Optimum-v0.3.21-linux-x64-overlay.tar.gz"), false)
})

test("needsUploadUrl enforces the 32 MB boundary", () => {
  assert.equal(needsUploadUrl(DIRECT_UPLOAD_LIMIT - 1), false)
  assert.equal(needsUploadUrl(DIRECT_UPLOAD_LIMIT), false)
  assert.equal(needsUploadUrl(DIRECT_UPLOAD_LIMIT + 1), true)
})

test("quotaDelayMs enforces public rate limit intervals", () => {
  assert.equal(quotaDelayMs(undefined, 1000), 0)
  assert.equal(quotaDelayMs(1000, 1000 + QUOTA_INTERVAL_MS), 0)
  assert.equal(quotaDelayMs(1000, 1000 + QUOTA_INTERVAL_MS + 5000), 0)
  assert.equal(quotaDelayMs(1000, 1000 + 5000), 10000)
})

test("countedEngines calculates detected and total engines", () => {
  const result = countedEngines({
    malicious: 1,
    suspicious: 1,
    undetected: 60,
    harmless: 8,
    timeout: 2
  })
  assert.deepEqual(result, { detected: 2, engines: 72 })

  const clean = countedEngines({
    malicious: 0,
    suspicious: 0,
    undetected: 70
  })
  assert.deepEqual(clean, { detected: 0, engines: 70 })
})

test("formatRow formats table rows for unscanned, completed scan, and report fallback", () => {
  const dummyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"

  // Unscanned (feed / metadata)
  const unscanned = formatRow({
    name: "assets.win.json",
    sha256: dummyHash,
    scan: undefined
  })
  assert.equal(unscanned, `| assets.win.json | \`${dummyHash}\` | not scanned |`)

  // Scan with engine counts
  const scanned = formatRow({
    name: "Optimum-Setup.exe",
    sha256: dummyHash,
    scan: { detected: 0, engines: 72 }
  })
  assert.equal(
    scanned,
    `| Optimum-Setup.exe | \`${dummyHash}\` | [0 of 72 engines](https://www.virustotal.com/gui/file/${dummyHash}) |`
  )

  // Scan fallback (no engine count available)
  const fallback = formatRow({
    name: "Optimum-Setup.exe",
    sha256: dummyHash,
    scan: {}
  })
  assert.equal(
    fallback,
    `| Optimum-Setup.exe | \`${dummyHash}\` | [report](https://www.virustotal.com/gui/file/${dummyHash}) |`
  )
})

test("buildSection includes table headers and advisory note", () => {
  const dummyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  const section = buildSection([
    { name: "assets.win.json", sha256: dummyHash, scan: undefined }
  ])

  assert.ok(section.includes(SECTION_HEADING))
  assert.ok(section.includes("| File | SHA-256 | VirusTotal |"))
  assert.ok(section.includes("| assets.win.json |"))
  assert.ok(section.includes("VirusTotal results are advisory"))
})

test("withSection appends or idempotently updates existing section", () => {
  const dummyHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
  const sec1 = buildSection([
    { name: "file1.exe", sha256: dummyHash, scan: undefined }
  ])
  const sec2 = buildSection([
    { name: "file1.exe", sha256: dummyHash, scan: { detected: 0, engines: 70 } },
    { name: "file2.deb", sha256: dummyHash, scan: undefined }
  ])

  // Case 1: Empty body
  assert.equal(withSection("", sec1), `${sec1}\n`)
  assert.equal(withSection(null, sec1), `${sec1}\n`)

  // Case 2: Body without existing section
  const initial = "Release v0.3.21\n\nChangelog notes here."
  const appended = withSection(initial, sec1)
  assert.equal(appended, `${initial}\n\n${sec1}\n`)

  // Case 3: Re-running replaces existing section idempotently
  const updated = withSection(appended, sec2)
  assert.ok(updated.includes(sec2))
  assert.ok(!updated.includes("file1.exe | `e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855` | not scanned"))
  assert.ok(updated.includes("Changelog notes here."))
  // Only one occurrence of SECTION_HEADING
  assert.equal(updated.split(SECTION_HEADING).length - 1, 1)

  // Case 4: Preserves subsequent sections
  const withSubsequent = `${initial}\n\n${sec1}\n\n## Next Section\n\nNext section body.`
  const updatedSubsequent = withSection(withSubsequent, sec2)
  assert.ok(updatedSubsequent.includes(sec2))
  assert.ok(updatedSubsequent.includes("## Next Section\n\nNext section body."))
  assert.equal(updatedSubsequent.split(SECTION_HEADING).length - 1, 1)
})

test("sha256Of correctly computes file digest", async () => {
  const tmp = mkdtempSync(join(tmpdir(), "release-test-"))
  try {
    const filePath = join(tmp, "hello.txt")
    writeFileSync(filePath, "hello world\n")
    const hash = await sha256Of(filePath)
    // sha256("hello world\n") = a948904f2f0f479b8f8197694b30184b0d2ed1c1cd2a1ec0fb85d299a192a447
    assert.equal(hash, "a948904f2f0f479b8f8197694b30184b0d2ed1c1cd2a1ec0fb85d299a192a447")
  } finally {
    rmSync(tmp, { recursive: true, force: true })
  }
})
