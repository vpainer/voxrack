# Установка / снятие GolosAPO на микрофон. Запускать от администратора:
#   powershell -ExecutionPolicy Bypass -File install-apo.ps1 [-Uninstall] [-Name "Shure MV7"]
# Что делает установка:
#   1. C:\Program Files\Golos\GolosAPO.dll + регистрация COM/APO (regsvr32);
#   2. C:\ProgramData\Golos (запись для пользователей и LOCAL SERVICE) + state.bin;
#   3. в FxProperties микрофона: наш CLSID как SFX для режима Default, «улучшения звука» включены;
#      что было до нас — сохраняется в HKLM\SOFTWARE\Golos\Backup\<endpoint> и возвращается при снятии;
#   4. перезапуск аудиослужб (звук пропадает на пару секунд).
param(
    [switch]$Uninstall,
    [string]$Name = 'Shure MV7'
)
$ErrorActionPreference = 'Stop'

$clsid   = '{BCC05645-2F4E-4C14-9C6E-24D2FCDE41DE}'
# GolosAPO.dll и golosctl.exe лежат рядом со скриптом (папка программы) или в ..\build (из исходников)
$dist    = if (Test-Path (Join-Path $PSScriptRoot 'GolosAPO.dll')) { $PSScriptRoot } else { Join-Path (Split-Path -Parent $PSScriptRoot) 'build' }
$prog    = Join-Path $env:ProgramFiles 'Golos'
$data    = Join-Path $env:ProgramData 'Golos'
$capRoot = 'SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Capture'
$bakRoot = 'SOFTWARE\Golos\Backup'

$vSfx      = '{d04e05a6-594b-4fb6-a80d-01af5eed7d1d},5'
$vSfxModes = '{d3993a3f-99c2-4402-b5ec-a92a0367664b},5'
$vNoFx     = '{1da5d803-d492-4edd-8c23-e0c0ffee7f0e},5'
$modeDefault = '{C18E2F7E-933D-4965-B7D1-1EEF228D2AF3}'

New-Item -ItemType Directory -Force $data | Out-Null
Start-Transcript -Path (Join-Path $data 'install.log') -Force | Out-Null

function Get-Endpoints {
    $hklm = [Microsoft.Win32.Registry]::LocalMachine
    $cap = $hklm.OpenSubKey($capRoot)
    foreach ($guid in $cap.GetSubKeyNames()) {
        $k = $cap.OpenSubKey($guid)
        $state = $k.GetValue('DeviceState')
        $props = $k.OpenSubKey('Properties')
        $dev = if ($props) { $props.GetValue('{b3f8fa53-0004-438e-9003-51a46e139bfc},6') } else { $null }
        if ($state -eq 1 -and $dev -and $dev -like "*$Name*") { [pscustomobject]@{ Guid = $guid; Device = $dev } }
    }
}

# FxProperties администратору можно только SetValue/QueryValue — открываем ключ ровно с этими правами
function Open-Fx($guid) {
    $rights = [System.Security.AccessControl.RegistryRights]'SetValue, QueryValues'
    $k = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$capRoot\$guid\FxProperties",
        [Microsoft.Win32.RegistryKeyPermissionCheck]::ReadWriteSubTree, $rights)
    if (-not $k) { throw "нет FxProperties у $guid" }
    $k
}

function Stop-Audio {
    Write-Host 'останавливаю аудиослужбы'
    Stop-Service Audiosrv -Force
    Stop-Service AudioEndpointBuilder -Force
}
function Start-Audio {
    Write-Host 'запускаю аудиослужбы'
    Start-Service AudioEndpointBuilder
    Start-Service Audiosrv
}

$eps = @(Get-Endpoints)
if ($eps.Count -eq 0) { throw "активный микрофон «$Name» не найден" }
$eps | ForEach-Object { Write-Host "микрофон: $($_.Device) $($_.Guid)" }

if (-not $Uninstall) {
    # папка данных: писать могут пользователи (окно «Голоса») и LOCAL SERVICE (audiodg)
    & icacls $data /grant '*S-1-5-32-545:(OI)(CI)M' '*S-1-5-19:(OI)(CI)M' | Out-Null
    & (Join-Path $dist 'golosctl.exe') init
    Get-ChildItem $data -File | ForEach-Object { & icacls $_.FullName /reset | Out-Null }

    Stop-Audio
    try {
        New-Item -ItemType Directory -Force $prog | Out-Null
        Copy-Item (Join-Path $dist 'GolosAPO.dll') $prog -Force
        $p = Start-Process regsvr32.exe -ArgumentList '/s', "`"$prog\GolosAPO.dll`"" -Wait -PassThru
        if ($p.ExitCode -ne 0) { throw "regsvr32: код $($p.ExitCode)" }

        foreach ($ep in $eps) {
            $fx = Open-Fx $ep.Guid
            $bak = [Microsoft.Win32.Registry]::LocalMachine.CreateSubKey("$bakRoot\$($ep.Guid)")
            if ($null -eq $bak.GetValue('Saved')) {
                foreach ($n in $vSfx, $vSfxModes, $vNoFx) {
                    $old = $fx.GetValue($n)
                    if ($null -ne $old) { $bak.SetValue($n, $old, $fx.GetValueKind($n)) }
                }
                $bak.SetValue('Saved', 1, 'DWord')
                Write-Host "сохранил исходные значения $($ep.Guid)"
            }
            $fx.SetValue($vSfx, $clsid, 'String')
            $fx.SetValue($vSfxModes, [string[]]@($modeDefault), 'MultiString')
            if ($null -ne $fx.GetValue($vNoFx)) { $fx.DeleteValue($vNoFx) }
            $fx.Close()
            Write-Host "APO поставлен на $($ep.Device)"
        }
    } finally {
        Start-Audio
    }
} else {
    Stop-Audio
    try {
        foreach ($ep in $eps) {
            $fx = Open-Fx $ep.Guid
            $bak = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey("$bakRoot\$($ep.Guid)")
            foreach ($n in $vSfx, $vSfxModes, $vNoFx) {
                $old = if ($bak) { $bak.GetValue($n) } else { $null }
                if ($null -ne $old) { $fx.SetValue($n, $old, $bak.GetValueKind($n)) }
                elseif ($null -ne $fx.GetValue($n)) { $fx.DeleteValue($n) }
            }
            $fx.Close()
            [Microsoft.Win32.Registry]::LocalMachine.DeleteSubKeyTree("$bakRoot\$($ep.Guid)", $false)
            Write-Host "APO снят с $($ep.Device)"
        }
        if (Test-Path "$prog\GolosAPO.dll") {
            Start-Process regsvr32.exe -ArgumentList '/u', '/s', "`"$prog\GolosAPO.dll`"" -Wait | Out-Null
            Remove-Item $prog -Recurse -Force
        }
    } finally {
        Start-Audio
    }
}
Write-Host 'готово'
Stop-Transcript | Out-Null
