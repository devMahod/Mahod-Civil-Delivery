#!/usr/bin/env bash
# Exit-code gate for the release chain (Codex 02:49). Every step keeps its full stdout/stderr in its own file; the REAL
# exit status of the command (not of a display filter) decides; nonzero or timeout (124) stops the chain at once.
# A step may also require a success line in its output (e.g. the dotnet "Passed!" summary): absent = failure.
# Usage (sourced):  GATE_LOG=<summary log> GATE_DIR=<dir for full outputs>
#   run_step <name> <required-regex or ""> <command...>

run_step() {
  local name=$1 need=$2
  shift 2
  local out="$GATE_DIR/$name.out"
  "$@" > "$out" 2>&1
  local rc=$?
  local summary
  summary=$(grep -E "Passed!|Failed!|\[FAIL\]|error CS|Build succeeded|Build FAILED" "$out" | head -5)
  if [ -n "$summary" ]; then
    printf '%s\n' "$summary" | sed "s/^/$name /" >> "$GATE_LOG"
  fi
  if [ $rc -ne 0 ]; then
    echo "$name STOP: exit $rc$([ $rc -eq 124 ] && echo ' (timeout)') — full output $out" >> "$GATE_LOG"
    return $rc
  fi
  if [ -n "$need" ] && ! grep -qE "$need" "$out"; then
    echo "$name STOP: exit 0 but required line /$need/ missing — full output $out" >> "$GATE_LOG"
    return 3
  fi
  if grep -qE "Failed!|\[FAIL\]|error CS|Build FAILED" "$out"; then
    echo "$name STOP: exit 0 but a failure line is present — full output $out" >> "$GATE_LOG"
    return 4
  fi
  echo "$name OK (exit 0)" >> "$GATE_LOG"
  return 0
}
