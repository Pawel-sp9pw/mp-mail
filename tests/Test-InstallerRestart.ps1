param(
    [string]$InnoCompiler = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = "Stop"
$repository = Split-Path $PSScriptRoot -Parent
$workspace = Join-Path $repository "artifacts/installer-tests/$([Guid]::NewGuid())"
$publish = Join-Path $workspace "publish"
New-Item -ItemType Directory -Path $workspace -Force | Out-Null

dotnet publish "$PSScriptRoot/InstallerProbe/InstallerProbe.csproj" -c Release -o $publish
if ($LASTEXITCODE -ne 0) { throw "Probe build failed." }

$source = Get-Content -Raw -LiteralPath "$repository/installer/mp-mail.iss"
$source = $source.Replace('AppId={{A02DE67B-3DA8-48A3-95B7-3CBA32F4CB0D}', 'AppId=MpMail-InstallerRestartTest')
$source = $source.Replace('[Setup]', "[Setup]`nUninstallable=no`nCreateAppDir=yes`nCreateUninstallRegKey=no`nUsePreviousAppDir=no`nUsePreviousTasks=no`nDisableProgramGroupPage=yes")
$source = $source.Replace('[Icons]', '[Icons]' + "`n#if 0").Replace('[Run]', "#endif`n[Run]")
$failureFile = 'Source: "{#PublishDir}\MpMail.dll"; DestDir: "{app}"; DestName: "blocked-file"; Flags: ignoreversion'

foreach ($scenario in @('success', 'failure')) {
    $script = Join-Path $workspace "$scenario.iss"
    $scenarioSource = $source
    if ($scenario -eq 'failure') {
        $scenarioSource = $source.Replace('[Icons]', "$failureFile`n[Icons]")
    }
    Set-Content -LiteralPath $script -Value $scenarioSource -Encoding utf8
    & $InnoCompiler /Qp "/DPublishDir=$publish" "/O$workspace" "/F$scenario" $script
    if ($LASTEXITCODE -ne 0) { throw "Installer compilation failed: $scenario." }

    foreach ($mode in @('/VERYSILENT', '/SILENT')) {
        $name = "$scenario-$($mode.TrimStart('/'))"
        $target = Join-Path $workspace "$name/MP Mail"
        $marker = Join-Path $target 'launches.txt'
        $log = Join-Path $workspace "$name.log"
        if ($scenario -eq 'failure') {
            New-Item -ItemType Directory -Path "$target/blocked-file" -Force | Out-Null
            Copy-Item -Path "$publish/*" -Destination $target
        }
        $arguments = @($mode, '/SUPPRESSMSGBOXES', '/NORESTART', '/CLOSEAPPLICATIONS',
            '/SP-', "/DIR=`"$target`"", "/LOG=`"$log`"")
        $process = Start-Process -FilePath "$workspace/$scenario.exe" -ArgumentList $arguments -WindowStyle Hidden -PassThru
        if (!$process.WaitForExit(60000)) {
            $process.Kill()
            throw "Installer timed out: $name. See $log"
        }
        if ($scenario -eq 'success') {
            if ($process.ExitCode -ne 0) { throw "Installer failed: $name, exit $($process.ExitCode). See $log" }
            $deadline = [DateTime]::UtcNow.AddSeconds(10)
            while (!(Test-Path -LiteralPath $marker) -and [DateTime]::UtcNow -lt $deadline) {
                Start-Sleep -Milliseconds 100
            }
            if (!(Test-Path -LiteralPath $marker)) { throw "Application did not restart: $name. See $log" }
            Start-Sleep -Seconds 1
            $launches = @(Get-Content -LiteralPath $marker)
            if ($launches.Count -ne 1 -or $launches[0] -ne "$target\MpMail.exe") {
                throw "Expected exactly one launch from the installed directory: $name."
            }
        } else {
            if ($process.ExitCode -eq 0) { throw "Failure fixture unexpectedly succeeded: $name." }
            if (!(Test-Path -LiteralPath "$target/MpMail.exe")) { throw "Existing application was not preserved after rollback: $name." }
            Start-Sleep -Seconds 2
            if (Test-Path -LiteralPath $marker) { throw "Application launched after failed installation: $name." }
        }
        Write-Host "PASS $name (installer exit $($process.ExitCode))"
    }
}
