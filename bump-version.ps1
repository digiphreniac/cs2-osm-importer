param(
    [ValidateSet('major', 'minor', 'patch')]
    [string]$Part = 'patch'
)

$repoRoot = $PSScriptRoot
$modJsonPath = Join-Path $repoRoot 'cs2-osm-importer/ui/mod.json'
$publishConfigPath = Join-Path $repoRoot 'cs2-osm-importer/Properties/PublishConfiguration.xml'

function Get-VersionParts {
    param([string]$Version)

    if ($Version -notmatch '^(\d+)\.(\d+)(?:\.(\d+))?$') {
        throw "Invalid version '$Version'. Expected major.minor or major.minor.patch."
    }

    $patch = if ($Matches[3]) { [int]$Matches[3] } else { 0 }
    return [pscustomobject]@{
        Major = [int]$Matches[1]
        Minor = [int]$Matches[2]
        Patch = $patch
    }
}

$modJson = Get-Content -Raw -Path $modJsonPath | ConvertFrom-Json
$current = Get-VersionParts -Version $modJson.version

switch ($Part) {
    'major' {
        $current.Major++
        $current.Minor = 0
        $current.Patch = 0
    }
    'minor' {
        $current.Minor++
        $current.Patch = 0
    }
    'patch' {
        $current.Patch++
    }
}

$newVersion = '{0}.{1}.{2}' -f $current.Major, $current.Minor, $current.Patch

$modJson.version = $newVersion
$modJson | ConvertTo-Json -Depth 10 | Set-Content -Path $modJsonPath -Encoding utf8

$publishXml = [xml](Get-Content -Raw -Path $publishConfigPath)
$publishXml.Publish.ModVersion.Value = $newVersion
$publishXml.Save($publishConfigPath)

Write-Host "Updated version to $newVersion"
