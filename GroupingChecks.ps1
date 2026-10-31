$ErrorActionPreference='Stop'
. "$PSScriptRoot\OcrGrouping.ps1"
function Line($text,$x,$y,$w,$h=40){[pscustomobject]@{text=$text;x=$x;y=$y;w=$w;h=$h}}
$wrapped=@(Merge-OcrLines @(
    (Line 'TOTAL EARNED' 303 147 288 30),
    (Line 'PROCEED' 495 303 207 36),
    (Line 'After each fight, visit the Shop to grow' 264 485 668),
    (Line 'stronger. Continue to the Shop.' 330 538 536)
))
if($wrapped.Count -ne 3 -or $wrapped[2].text -ne 'After each fight, visit the Shop to grow stronger. Continue to the Shop.'){throw 'Centered tooltip was not joined correctly'}
if($wrapped[2].x -ne 264 -or $wrapped[2].w -ne 668 -or $wrapped[2].h -ne 93){throw 'Paragraph bounds incorrect'}
$columns=@(Merge-OcrLines @((Line 'Left column first' 20 20 200),(Line 'Right column first' 350 20 220),(Line 'Left second' 20 70 160),(Line 'Right second' 350 70 180)))
if($columns.Count -ne 2 -or $columns[0].text -like '*Right*' -or $columns[1].text -like '*Left*'){throw 'Separate UI columns merged'}
$sizes=@(Merge-OcrLines @((Line 'BIG TITLE' 40 10 300 60),(Line 'Small description' 40 77 300 25)))
if($sizes.Count -ne 2){throw 'Large title merged with body'}
$spaced=@(Merge-OcrLines @((Line 'First paragraph.' 20 20 300),(Line 'Second paragraph.' 20 150 300)))
if($spaced.Count -ne 2){throw 'Distant paragraphs merged'}
$shift=@(Merge-OcrLines @((Line 'A line' 100 10 220),(Line 'continuation' 80 60 260),(Line 'ending.' 110 110 200)))
if($shift.Count -ne 1 -or $shift[0].x -ne 80 -or $shift[0].w -ne 260){throw 'Three-line paragraph union incorrect'}
$fragments=@(Merge-OcrLines @((Line 'After each fight, visit the Shop to grow' 263 484 668 42),(Line 'Continue to the Shop.' 516 536 339 41),(Line 'stronger.' 329 545 151 33)))
if($fragments.Count -ne 1 -or $fragments[0].text -ne 'After each fight, visit the Shop to grow stronger. Continue to the Shop.'){throw 'Split visual row was not reconstructed'}
'PASS: centered wrap, button separation, columns, type sizes, paragraph gaps, union bounds, split OCR row.'
