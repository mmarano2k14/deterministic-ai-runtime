$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
python (Join-Path $root "launcher\launcher.py") @args
