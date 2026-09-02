# Refuses to let a commit carry anything that should not be public.
#
# The beta build name and its download link were shared in confidence. Personal paths and
# project names are nobody else's business. A `git add -A` once nearly published somebody's
# artwork, which is why this exists rather than a habit of being careful.

$patterns = @(
    '27-08-2026',                 # dated beta device profile
    'release\.lightburnsoftware\.com/private',
    'HRMC',
    'Motorcycles We Trust',
    'SwiftDATA',
    '//cortex',
    'Hi-ROLLERS'
)

Push-Location (Split-Path -Parent $PSScriptRoot)
try {
    # Two files are excluded from the scan, both deliberately.
    #
    # This script, because it necessarily contains every string it is looking for.
    #
    # .gitignore, because an entry there is a filename the repository is being told never to
    # track. A private name in the ignore list is the opposite of a leak - it is the mechanism
    # that keeps the file itself out.
    $diff = git diff --cached -- . ':(exclude)tests/privacy-check.ps1' ':(exclude).gitignore'
    $bad = 0
    foreach ($p in $patterns) {
        $hits = $diff | Select-String -Pattern $p
        if ($hits) {
            Write-Output ("BLOCKED  '{0}' appears in the staged diff:" -f $p)
            $hits | Select-Object -First 3 | ForEach-Object { Write-Output ("    " + $_.Line.Trim()) }
            $bad++
        }
    }

    if ($bad -eq 0) {
        Write-Output 'Privacy check passed: nothing private in the staged diff.'
        exit 0
    }
    Write-Output "$bad pattern(s) blocked. Unstage them before committing."
    exit 1
}
finally { Pop-Location }
