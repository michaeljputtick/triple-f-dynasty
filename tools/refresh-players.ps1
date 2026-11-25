# Pulls Sleeper's full NFL player list (~15MB) and writes a trimmed copy to
# wwwroot/data/players.json (id -> name, positions, birth date) so the site doesn't have to.
# Sleeper asks that this endpoint be called at most once a day; rerun it a few times a season.

$out = Join-Path $PSScriptRoot '..\wwwroot\data\players.json'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

$all = Invoke-RestMethod https://api.sleeper.app/v1/players/nfl
$keep = 'QB', 'RB', 'WR', 'TE', 'K', 'DEF'

$trimmed = [ordered]@{}
foreach ($prop in $all.PSObject.Properties) {
    $p = $prop.Value
    $positions = @($p.fantasy_positions | Where-Object { $keep -contains $_ })
    if (-not $positions -and $keep -notcontains $p.position) { continue }
    if (-not $positions) { $positions = @($p.position) }
    $name = if ($p.full_name) { $p.full_name } else { "$($p.first_name) $($p.last_name)".Trim() }
    $entry = [ordered]@{ n = $name; p = $positions }
    if ($p.birth_date) { $entry.b = $p.birth_date } # used for age curves in team value
    $trimmed[$prop.Name] = $entry
}

$trimmed | ConvertTo-Json -Depth 4 -Compress | Set-Content -Encoding utf8 $out
"Wrote $($trimmed.Count) players to $out ($([math]::Round((Get-Item $out).Length / 1KB)) KB)"
