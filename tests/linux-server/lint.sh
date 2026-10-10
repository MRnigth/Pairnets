#!/usr/bin/env bash
# The shell lint CI runs (.github/workflows/ci.yml), for the Linux test box: shellcheck over every shell
# script in the repo, and a syntax check of the test box's Python fakes.
#
#   tests/linux-server/lint.sh
set -euo pipefail
cd "$(dirname "${BASH_SOURCE[0]}")/../.."
shopt -s nullglob
files=(deploy/*.sh deploy/*/*.sh scripts/*.sh tests/linux-server/*.sh)
echo "$(shellcheck --version | sed -n 's/^version: /shellcheck /p') over ${#files[@]} scripts"
shellcheck "${files[@]}"
echo "shellcheck: clean"
python3 -I -c 'import ast, sys
for f in sys.argv[1:]:
    ast.parse(open(f, encoding="utf-8").read(), f)
print("python: %d files parse" % (len(sys.argv) - 1))' tests/linux-server/*.py
