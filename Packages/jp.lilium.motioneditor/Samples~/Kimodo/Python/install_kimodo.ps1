<#
Installs Kimodo into a Python environment for the Kimodo sample of Lilium Motion Editor, in one go (Windows, NVIDIA GPU).
It does step 2 of the setup ("Install Kimodo"):
  1. checks git, uv and the C++ compiler (Visual Studio Build Tools), and offers to install the missing ones with winget
  2. downloads Kimodo into <Folder>\kimodo (git clone)
  3. makes the Python environment <Folder>\kimodo\.venv (Python 3.11)
  4. installs PyTorch (CUDA 12.4), CMake and Kimodo
  5. signs in to Hugging Face (hf auth login) unless a token is already there
  6. runs Kimodo once to make the T-pose BVH (<Folder>\kimodo\tpose.bvh); this downloads the models (about 20 GB)
     and Unity makes Kimodo's skeleton from it ("Check That It Works")
Steps that are already done are skipped, so it can be run again after a failure.

  powershell -ExecutionPolicy Bypass -File install_kimodo.ps1 -Folder C:\Tools [-Language ja] [-Check] [-Yes]

-Check only reports what is installed and changes nothing. -Yes does not ask before downloading.
The Kimodo panel of Unity runs this from "Install Everything" and sets the python.exe it makes.
#>
param (
    [string] $Folder = (Get-Location).Path,
    [string] $Language = "en",
    [string] $HuggingFaceHome = "",
    [switch] $Check,
    [switch] $Yes
)

$ErrorActionPreference = "Stop"

# The words of this script (the Kimodo panel passes its language)
$Text = @{
    en = @{
        Title = "Install Kimodo (Lilium Motion Editor)"
        Where = "Kimodo goes into: {0}"
        About = "This downloads about 25 GB in all and can take 30 minutes to an hour. Steps that are already done are skipped."
        Continue = "Continue? (y/n)"
        Cancelled = "Stopped. Nothing more was changed."
        Ok = "  OK      {0}"
        Missing = "  Missing {0}"
        NoGpu = "No NVIDIA graphics card was found (nvidia-smi). Kimodo needs one; installing may work, but making motions will not."
        AskInstall = "{0} is not installed. Install it now with winget? (y/n)"
        Installing = "Installing {0}..."
        VsNote = "The C++ compiler (Visual Studio Build Tools, about 6 GB) needs administrator rights: allow it if Windows asks."
        NoWinget = "winget is not available. Install {0} yourself ({1}) and run this again."
        Manual = "Install {0} yourself ({1}) and run this again."
        Step = "[{0}/6] {1}"
        Clone = "Downloading Kimodo"
        Venv = "Making the Python environment"
        Torch = "Installing PyTorch (about 2.5 GB)"
        Kimodo = "Installing Kimodo (this builds a small C++ part)"
        Login = "Signing in to Hugging Face"
        TPose = "Running Kimodo for the first time (downloads the models, about 20 GB)"
        Skip = "  already done"
        HfHave = "  a Hugging Face token is already there"
        HfHow = "  Paste the Read token you made on Hugging Face and press Enter (nothing shows while you paste). Answer n to 'Add token as git credential?'."
        CheckDone = "Check only: nothing was changed."
        Done = "Done. In Unity, press 'Check That It Works' in the Kimodo Install window (Python: {0})."
        Failed = "Stopped because a step failed: {0}"
    }
    ja = @{
        Title = "Kimodo を入れる（Lilium Motion Editor）"
        Where = "Kimodo を入れる場所: {0}"
        About = "合わせて約 25GB をダウンロードするので、30 分〜1 時間かかることがあります。済んでいる手順は飛ばします。"
        Continue = "続けますか？ (y/n)"
        Cancelled = "止めました。これ以上は何も変えていません。"
        Ok = "  あり    {0}"
        Missing = "  なし    {0}"
        NoGpu = "NVIDIA のグラフィックボードが見つかりません（nvidia-smi）。Kimodo には必要です。入れることはできても、動きは作れません。"
        AskInstall = "{0} が入っていません。winget で今入れますか？ (y/n)"
        Installing = "{0} を入れています..."
        VsNote = "C++ のコンパイラー（Visual Studio Build Tools。約 6GB）は管理者の許可が要ります。Windows に聞かれたら許可してください。"
        NoWinget = "winget が使えません。{0} を自分で入れてから（{1}）、もう一度実行してください。"
        Manual = "{0} を自分で入れてから（{1}）、もう一度実行してください。"
        Step = "[{0}/6] {1}"
        Clone = "Kimodo をダウンロード"
        Venv = "Python の環境を作る"
        Torch = "PyTorch を入れる（約 2.5GB）"
        Kimodo = "Kimodo を入れる（小さな C++ の部品を組み立てます）"
        Login = "Hugging Face にログイン"
        TPose = "Kimodo を初めて動かす（モデルを約 20GB ダウンロードします）"
        Skip = "  済んでいます"
        HfHave = "  Hugging Face の合言葉（トークン）はもうあります"
        HfHow = "  Hugging Face で作った Read の合言葉（トークン）を貼り付けて Enter を押してください（貼り付けても何も表示されません）。「Add token as git credential?」には n と答えます。"
        CheckDone = "確かめただけです。何も変えていません。"
        Done = "できました。Unity の Kimodo のインストールの窓で「使えるか確かめる」を押してください（Python: {0}）。"
        Failed = "うまくいかなかったので止めました: {0}"
    }
}
if (-not $Text.ContainsKey($Language)) { $Language = "en" }

function Say ([string] $key) {
    $format = $Text[$Language][$key]
    if ($args.Count -gt 0) { $format = $format -f $args }
    Write-Host $format
}

function Ask ([string] $key) {
    if ($Yes) { return $true }
    $format = $Text[$Language][$key]
    if ($args.Count -gt 0) { $format = $format -f $args }
    return (Read-Host $format) -match "^[yY]"
}

# Runs a program and stops when it fails (native programs do not stop the script on their own)
function Run ([string] $exe) {
    & $exe @args
    if ($LASTEXITCODE -ne 0) { throw "$exe $($args -join ' ') (exit code $LASTEXITCODE)" }
}

# After winget installs something, read PATH again (this window still has the old one)
function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable("Path", "Machine") + ";" + [Environment]::GetEnvironmentVariable("Path", "User")
}

function Has-Command ([string] $name) {
    return $null -ne (Get-Command $name -ErrorAction SilentlyContinue)
}

function Has-Compiler {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $false }
    $found = & $vswhere -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    return -not [string]::IsNullOrWhiteSpace($found)
}

$kimodo = Join-Path $Folder "kimodo"
$venv = Join-Path $kimodo ".venv"
$python = Join-Path $venv "Scripts\python.exe"
$hfHome = if ($HuggingFaceHome) { $HuggingFaceHome } elseif ($env:HF_HOME) { $env:HF_HOME } else { Join-Path $env:USERPROFILE ".cache\huggingface" }
$token = Join-Path $hfHome "token"
# Unity makes Kimodo's skeleton from this file (KimodoSetup looks for it next to the Python environment)
$tpose = Join-Path $kimodo "tpose.bvh"

function Has-Torch {
    if (-not (Test-Path $python)) { return $false }
    & $python -c "import sys, torch; sys.exit(0 if torch.__version__.startswith('2.6.0') and torch.version.cuda else 1)" 2>$null
    return $LASTEXITCODE -eq 0
}

function Has-Kimodo {
    if (-not (Test-Path $python)) { return $false }
    & $python -c "import importlib.util, sys; sys.exit(0 if importlib.util.find_spec('kimodo') and importlib.util.find_spec('motion_correction') else 3)" 2>$null
    return $LASTEXITCODE -eq 0
}

try {
    Say Title
    Say Where $kimodo
    Write-Host ""

    # What is there already
    $tools = @(
        @{ Name = "NVIDIA GPU (nvidia-smi)"; Have = (Has-Command "nvidia-smi") },
        @{ Name = "git"; Have = (Has-Command "git") },
        @{ Name = "uv"; Have = (Has-Command "uv") },
        @{ Name = "C++ compiler (Visual Studio Build Tools)"; Have = (Has-Compiler) },
        @{ Name = "Kimodo ($kimodo)"; Have = (Test-Path (Join-Path $kimodo ".git")) },
        @{ Name = "Python environment ($python)"; Have = (Test-Path $python) },
        @{ Name = "PyTorch 2.6 (CUDA)"; Have = (Has-Torch) },
        @{ Name = "Kimodo in the Python environment"; Have = (Has-Kimodo) },
        @{ Name = "Hugging Face token ($token)"; Have = (Test-Path $token) },
        @{ Name = "T-pose BVH ($tpose)"; Have = (Test-Path $tpose) }
    )
    foreach ($tool in $tools) {
        if ($tool.Have) { Say Ok $tool.Name } else { Say Missing $tool.Name }
    }
    Write-Host ""
    if ($Check) {
        Say CheckDone
        exit 0
    }
    if (-not $tools[0].Have) { Say NoGpu }
    Say About
    if (-not (Ask Continue)) {
        Say Cancelled
        exit 1
    }

    # Tools: git, uv and the C++ compiler (winget ids)
    $needed = @(
        @{ Name = "git"; Have = { Has-Command "git" }; Id = "Git.Git"; Url = "https://git-scm.com/download/win"; Override = $null },
        @{ Name = "uv"; Have = { Has-Command "uv" }; Id = "astral-sh.uv"; Url = "https://docs.astral.sh/uv/"; Override = $null },
        @{ Name = "Visual Studio Build Tools (C++)"; Have = { Has-Compiler }; Id = "Microsoft.VisualStudio.2022.BuildTools"; Url = "https://visualstudio.microsoft.com/visual-cpp-build-tools/";
           Override = "--passive --wait --add Microsoft.VisualStudio.Workload.VCTools --includeRecommended" }
    )
    foreach ($tool in $needed) {
        if (& $tool.Have) { continue }
        if (-not (Has-Command "winget")) { throw ($Text[$Language].NoWinget -f $tool.Name, $tool.Url) }
        if (-not (Ask AskInstall $tool.Name)) { throw ($Text[$Language].Manual -f $tool.Name, $tool.Url) }
        Say Installing $tool.Name
        if ($tool.Override) {
            Say VsNote
            Run winget install --id $tool.Id -e --accept-source-agreements --accept-package-agreements --override $tool.Override
        }
        else {
            Run winget install --id $tool.Id -e --accept-source-agreements --accept-package-agreements
        }
        Refresh-Path
        if (-not (& $tool.Have)) { throw ($Text[$Language].Manual -f $tool.Name, $tool.Url) }
    }

    Say Step 1 $Text[$Language].Clone
    if (Test-Path (Join-Path $kimodo ".git")) { Say Skip }
    else {
        New-Item -ItemType Directory -Force -Path $Folder | Out-Null
        Run git clone -c core.autocrlf=false https://github.com/nv-tlabs/kimodo.git $kimodo
    }

    Say Step 2 $Text[$Language].Venv
    if (Test-Path $python) { Say Skip }
    else { Run uv venv $venv --python 3.11 }

    Say Step 3 $Text[$Language].Torch
    if (Has-Torch) { Say Skip }
    else { Run uv pip install --python $python torch==2.6.0 --index-url https://download.pytorch.org/whl/cu124 }

    Say Step 4 $Text[$Language].Kimodo
    if (Has-Kimodo) { Say Skip }
    else {
        Run uv pip install --python $python cmake
        # Kimodo builds motion_correction with the CMake of the environment
        $env:Path = (Join-Path $venv "Scripts") + ";" + $env:Path
        Push-Location $kimodo
        try { Run uv pip install --python $python -e . }
        finally { Pop-Location }
        if (-not (Has-Kimodo)) { throw "kimodo / motion_correction" }
    }

    Say Step 5 $Text[$Language].Login
    if (Test-Path $token) { Say HfHave }
    else {
        Say HfHow
        $env:HF_HOME = $hfHome
        Run (Join-Path $venv "Scripts\hf.exe") auth login
    }

    Say Step 6 $Text[$Language].TPose
    if (Test-Path $tpose) { Say Skip }
    else {
        # the same settings as the Kimodo panel (the text model on the CPU)
        $env:HF_HOME = $hfHome
        $env:TEXT_ENCODER_MODE = "local"
        $env:TEXT_ENCODER_DEVICE = "cpu"
        Push-Location $kimodo
        try { Run (Join-Path $venv "Scripts\kimodo_gen.exe") "a person stands still" --duration 1 --output tpose --bvh --bvh_standard_tpose }
        finally { Pop-Location }
        if (-not (Test-Path $tpose)) { throw "tpose.bvh" }
    }

    Write-Host ""
    Say Done $python
    exit 0
}
catch {
    Write-Host ""
    Say Failed $_.Exception.Message
    exit 1
}
