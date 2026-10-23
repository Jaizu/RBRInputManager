<#
  Used by the CopyToDevInstall target so a Debug build replaces the running dev copy (app\RBRInputManager.exe).

  -Stop   Does nothing if no file in -Source differs from the dev copy (same size and time as the Copy task's
          SkipUnchangedFiles), so a build with nothing new keeps the app open. Otherwise asks the running dev copy to close, like clicking X: with unsaved binding edits the app asks first, so
          nothing is lost. If it is still open after the timeout (the question is unanswered, or it hangs), it is
          left alone and the copy fails with the usual "file in use" warnings. When it closes, a marker remembers
          that it has to be reopened.
  -Start  Reopens the new copy if -Stop closed it.
#>
param(
    [Parameter(Mandatory)] [string] $Exe,
    [Parameter(Mandatory)] [string] $Marker,
    [string] $Source,
    [switch] $Stop,
    [switch] $Start,
    [int] $TimeoutSeconds = 15
)

$Exe = [System.IO.Path]::GetFullPath($Exe)

if ($Stop) {
    Remove-Item $Marker -ErrorAction SilentlyContinue
    $running = @(Get-Process -Name ([System.IO.Path]::GetFileNameWithoutExtension($Exe)) -ErrorAction SilentlyContinue |
                 Where-Object { $_.Path -and [string]::Equals($_.Path, $Exe, [StringComparison]::OrdinalIgnoreCase) })
    if ($running.Count -eq 0) { exit 0 }

    if ($Source) {
        $Source = [System.IO.Path]::GetFullPath($Source).TrimEnd('\')
        $dest = [System.IO.Path]::GetDirectoryName($Exe)
        $changed = Get-ChildItem -Path $Source -Recurse -File | Where-Object {
            $target = Get-Item -LiteralPath (Join-Path $dest $_.FullName.Substring($Source.Length + 1)) -ErrorAction SilentlyContinue
            -not $target -or $target.Length -ne $_.Length -or $target.LastWriteTimeUtc -ne $_.LastWriteTimeUtc
        } | Select-Object -First 1
        if (-not $changed) { exit 0 }
    }

    Write-Host "Closing the running dev copy so it can be replaced..."
    foreach ($p in $running) { [void]$p.CloseMainWindow() }
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    foreach ($p in $running) {
        $left = [int][Math]::Max(0, ($deadline - (Get-Date)).TotalMilliseconds)
        if (-not $p.WaitForExit($left)) {
            Write-Host "warning: the dev copy did not close within $TimeoutSeconds s (unsaved changes?). It was left open and not updated."
            exit 0
        }
    }
    New-Item -ItemType File -Path $Marker -Force | Out-Null
    exit 0
}

if ($Start -and (Test-Path $Marker)) {
    Remove-Item $Marker
    Start-Process -FilePath $Exe -WorkingDirectory ([System.IO.Path]::GetDirectoryName($Exe))
    Write-Host "Dev copy reopened."
}
exit 0
