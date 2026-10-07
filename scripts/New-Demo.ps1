param([Parameter(Mandatory = $true)][string]$OutputDirectory)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$demoSampleRate = 22050
$demoDurationSeconds = 20
$demoSampleCount = $demoSampleRate * $demoDurationSeconds
$demoStream = [System.IO.File]::Create((Join-Path $OutputDirectory 'sandbox.wav'))
$demoWriter = [System.IO.BinaryWriter]::new($demoStream)
try {
    $demoWriter.Write([System.Text.Encoding]::ASCII.GetBytes('RIFF'))
    $demoWriter.Write([int](36 + $demoSampleCount * 2))
    $demoWriter.Write([System.Text.Encoding]::ASCII.GetBytes('WAVEfmt '))
    $demoWriter.Write([int]16)
    $demoWriter.Write([short]1)
    $demoWriter.Write([short]1)
    $demoWriter.Write([int]$demoSampleRate)
    $demoWriter.Write([int]($demoSampleRate * 2))
    $demoWriter.Write([short]2)
    $demoWriter.Write([short]16)
    $demoWriter.Write([System.Text.Encoding]::ASCII.GetBytes('data'))
    $demoWriter.Write([int]($demoSampleCount * 2))
    for ($demoIndex = 0; $demoIndex -lt $demoSampleCount; $demoIndex++) {
        $demoTime = $demoIndex / [double]$demoSampleRate
        $demoPhase = $demoTime % 0.5
        $demoEnvelope = [Math]::Exp(-$demoPhase * 35)
        $demoFrequency = if (([int][Math]::Floor($demoTime * 2) % 4) -eq 0) { 880 } else { 440 }
        $demoValue = [Math]::Sin(2 * [Math]::PI * $demoFrequency * $demoTime) * $demoEnvelope * 6500
        $demoWriter.Write([short]$demoValue)
    }
}
finally { $demoWriter.Dispose() }
$demoNotes = [System.Collections.Generic.List[string]]::new()
$demoColumns = @(0,1,2,3,4,5,6,5,4,3,2,1)
for ($demoNoteIndex = 0; $demoNoteIndex -lt 64; $demoNoteIndex++) {
    $demoLane = $demoColumns[$demoNoteIndex % $demoColumns.Count]
    $demoX = [int][Math]::Floor(($demoLane + 0.5) * 512 / 7)
    $demoTimeMs = 1000 + $demoNoteIndex * 250
    if (($demoNoteIndex % 12) -eq 3) {
        $demoEndMs = $demoTimeMs + 500
        $demoNotes.Add("$demoX,192,$demoTimeMs,128,0,${demoEndMs}:0:0:0:0:")
    } else { $demoNotes.Add("$demoX,192,$demoTimeMs,1,0,0:0:0:0:") }
}
$demoChart = @'
osu file format v14

[General]
AudioFilename: sandbox.wav
PreviewTime: 1000
Mode: 3
SampleSet: Soft

[Editor]
Bookmarks: 1000,9000
BeatDivisor: 4

[Metadata]
Title: 7K Sandbox
Artist: SimpleManiaEditor
Creator: Mapper
Version: 7K Practice
Source: Generated practice audio
Tags:
BeatmapID:-1
BeatmapSetID:-1

[Difficulty]
HPDrainRate:5
CircleSize:7
OverallDifficulty:5
ApproachRate:5
SliderMultiplier:1.4
SliderTickRate:1

[Events]

[TimingPoints]
0,500,4,2,0,70,1,0

[HitObjects]
'@
$demoChart = $demoChart + "`n" + ($demoNotes -join "`n") + "`n"
[System.IO.File]::WriteAllText((Join-Path $OutputDirectory '7K Sandbox.osu'), $demoChart, [System.Text.UTF8Encoding]::new($false))
