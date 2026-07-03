#!/usr/bin/env python3
"""Rewrite direct reads of custom-accessor events to their m_<Name> backing field.

ILSpy decompiles `public event T Name { add { ... } remove { ... } }` events
correctly, but the call sites that read the event's current value (null
checks, casts, GetInvocationList(), etc.) still reference the event name
directly. A custom-accessor event has no backing field of its own name, so
any use other than += or -= is CS0079. Every such event in this codebase
follows the same Interlocked.CompareExchange pattern with a private
`m_<Name>` field, so rewriting reads to that field is safe everywhere it
matches.
"""
import re
import sys
import glob

STRING_RE = re.compile(r'"(?:[^"\\]|\\.)*"')


def main(roots):
    files = []
    for root in roots:
        files.extend(glob.glob(root + "/**/*.cs", recursive=True))

    total_files_changed = 0
    total_subs = 0

    for path in files:
        with open(path, encoding="utf-8") as f:
            text = f.read()

        backing = set(re.findall(r'private\s+[\w<>,\.\[\]\s]+?\s+m_(\w+);', text))
        if not backing:
            continue

        # Async state machines capture `this` into a `_003C_003E4__this` field and
        # commonly alias it to a locally-scoped variable at the top of MoveNext
        # (`TYPE varname = _003C_003E4__this;` or `... = someDisplayClass._003C_003E4__this;`).
        # That variable IS `this` for this class, so qualifying an event read
        # through it needs the same rewrite as `this.Name`.
        this_aliases = set(re.findall(r'\b(\w+)\s*=\s*[\w.]*_003C_003E4__this;', text))
        allowed_qualifiers = {'this'} | this_aliases

        lines = text.split('\n')
        file_changed = False

        for name in sorted(backing, key=len, reverse=True):
            decl_re = re.compile(r'\bevent\s+\S.*\b' + re.escape(name) + r'\b')
            if not decl_re.search(text):
                continue
            # Match an optional `qualifier.` prefix so it can be inspected: only
            # rewrite a bare reference (no qualifier) or one qualified by `this`
            # or a this-alias - never `SomeOtherType.Name`, which is a different
            # member entirely (e.g. an enum value) that happens to share the
            # event's name.
            read_re = re.compile(
                r'\b(?:(\w+)\.)?' + re.escape(name) + r'\b(?!\s*[+\-]=)'
            )
            for i, line in enumerate(lines):
                if decl_re.search(line):
                    continue
                spans = [m.span() for m in STRING_RE.finditer(line)]

                def in_string(pos):
                    return any(a <= pos < b for a, b in spans)

                out = []
                last = 0
                n = 0
                for m in read_re.finditer(line):
                    if in_string(m.start()):
                        continue
                    qualifier = m.group(1)
                    if qualifier is not None and qualifier not in allowed_qualifiers:
                        continue
                    prefix = f'{qualifier}.' if qualifier else ''
                    out.append(line[last:m.start()])
                    out.append(prefix + 'm_' + name)
                    last = m.end()
                    n += 1
                out.append(line[last:])
                if n:
                    lines[i] = ''.join(out)
                    file_changed = True
                    total_subs += n

        if file_changed:
            with open(path, 'w', encoding='utf-8') as f:
                f.write('\n'.join(lines))
            total_files_changed += 1

    print(f"Rewrote {total_subs} event read(s) across {total_files_changed} file(s).")


if __name__ == "__main__":
    main(sys.argv[1:])
