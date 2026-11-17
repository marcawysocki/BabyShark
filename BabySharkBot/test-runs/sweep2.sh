#!/bin/bash
cd /c/Users/marca/source/repos/BabyShark
export ProgramData='C:\ProgramData'
DOTNET="/c/Program Files/dotnet/dotnet.exe"
BASE="--seed=3 --patternjit --juggler --t2walk=BA --s1f0=126.0,20.0 --sasmart=13 --moveprobe --probeframes=7,8 --s3stopbump --hatchbump --s1stopat=105 --suicide-end --nocolavoid --bumpviz --saex=8 --tealf0 --ybf0 --tealtest --t1tbflip"
run() {
  local name="$1"; shift
  local extra="$1"
  "$DOTNET" run --project BabySharkBot/BabySharkBot.csproj --no-build -- $BASE $extra > "BabySharkBot/test-runs/sweep2-$name.log" 2>&1
  local line=$(grep 'T1 FIRST FLIP ON TB\|T3 FIRST FLIP ON TA\|trigger=' "BabySharkBot/test-runs/sweep2-$name.log" | head -3 | tr '\n' ' | ')
  echo "=== $name [$extra] => $line"
}
run W1-baseline ""
run W2-t3movf6 "--t3movf=6"
run W3-thru-south "--tealthru=0.5,-0.2"
run W4-s3-deep "--s3thru=0.5,-1.5"
echo "SWEEP2 COMPLETE"
