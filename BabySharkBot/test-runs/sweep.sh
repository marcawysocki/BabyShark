#!/bin/bash
cd /c/Users/marca/source/repos/BabyShark
export ProgramData='C:\ProgramData'
DOTNET="/c/Program Files/dotnet/dotnet.exe"
BASE="--seed=3 --patternjit --juggler --t2walk=BA --s1f0=126.0,20.0 --sasmart=13 --moveprobe --probeframes=7,8 --s3stopbump --hatchbump --s1stopat=105 --suicide-end --nocolavoid --bumpviz --saex=8 --tealf0 --ybf0 --tealtest"
run() {
  local name="$1"; shift
  local extra="$1"
  "$DOTNET" run --project BabySharkBot/BabySharkBot.csproj --no-build -- $BASE $extra > "BabySharkBot/test-runs/sweep-$name.log" 2>&1
  local flip=$(grep -oE 'T3 FIRST FLIP ON TA' "BabySharkBot/test-runs/sweep-$name.log" | head -1)
  local line=$(grep 'T3 FIRST FLIP ON TA\|SUICIDE-END FAIL-OPEN\|trigger=T3 flip' "BabySharkBot/test-runs/sweep-$name.log" | head -2 | tr '\n' ' | ')
  echo "=== $name [$extra] => $line"
}
run A-t3movf6 "--t3movf=6"
run B-t3movf5 "--t3movf=5"
run C-t3movf4 "--t3movf=4"
run D-thru-south "--tealthru=0.5,-0.2"
run E-t1-north "--t1revoff=-3,1"
run F-walk-sa "--t3walk=SA"
run G-walk-sb "--t3walk=SB"
run H-s3-deep "--s3thru=0.5,-1.5"
echo "SWEEP COMPLETE"
