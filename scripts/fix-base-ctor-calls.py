#!/usr/bin/env python3
"""Rewrite `base._002Ector(args);` / `this._002Ector(args);` statements into a
proper `: base(args)` / `: this(args)` constructor initializer.

ILSpy occasionally decompiles the base/this constructor chain call as a plain
method-call statement using its IL name (`.ctor`, mangled to `_002Ector`)
instead of C#'s `: base(...)`/`: this(...)` syntax, which is the only legal
way to write it. The call can appear anywhere in the decompiled body, not
just as the first statement (ILSpy sometimes shows field-initializer-like
code first); moving it to the initializer position doesn't change what that
code does, since real field initializers always run after the base
constructor per C# spec, which is the order this produces either way.
"""
import re
import sys
import glob

CALL_RE = re.compile(r'[ \t]*(base|this)\._002Ector\(((?:[^()]|\([^()]*\))*)\);\n')
SIG_RE = re.compile(
    r'((?:public|private|protected|internal|static)[ \w]*\s\w+\(([^()]*)\)\s*\n)(\t*\{\n)'
)


def fix_once(text):
    """Fix the first constructor (by textual order) that still has a bare
    _002Ector call in its body. Returns (new_text, changed)."""
    for sig_m in SIG_RE.finditer(text):
        body_start = sig_m.end()
        depth = 1
        j = body_start
        while depth > 0 and j < len(text):
            if text[j] == '{':
                depth += 1
            elif text[j] == '}':
                depth -= 1
            j += 1
        body = text[body_start:j]
        call_m = CALL_RE.search(body)
        if not call_m:
            continue
        kind, args = call_m.group(1), call_m.group(2)
        new_body = body[:call_m.start()] + body[call_m.end():]
        header, brace_line = sig_m.group(1), sig_m.group(3)
        new_header = header.rstrip('\n') + f'\n\t\t: {kind}({args})\n'
        return text[:sig_m.start()] + new_header + brace_line + new_body + text[j:], True
    return text, False


def main(roots):
    files = []
    for root in roots:
        files.extend(glob.glob(root + "/**/*.cs", recursive=True))

    total_files_changed = 0

    for path in files:
        with open(path, encoding="utf-8") as f:
            text = f.read()
        if '_002Ector(' not in text:
            continue
        original = text
        for _ in range(50):
            text, changed = fix_once(text)
            if not changed:
                break
        if text != original:
            with open(path, "w", encoding="utf-8") as f:
                f.write(text)
            total_files_changed += 1

    print(f"Rewrote base/this constructor calls in {total_files_changed} file(s).")


if __name__ == "__main__":
    main(sys.argv[1:])
