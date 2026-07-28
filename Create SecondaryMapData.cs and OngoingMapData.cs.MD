Create SecondaryMapData.cs and OngoingMapData.cs

Existing  .cs files should not need changing for these two new files. Except for settings and BabySharkMiningManager.cs
Explain Your understanding before coding. 

SecondaryMapData.cs
WorkInProcess should now be here.

When the manager and program.Cs determine that a map ( Settings.MapDataLoaded ) has already been played but a new spawn location is now being played on that map Then we need secondary map data which do the same greedy ordering of workers and mineral labels as initial map data for the new location and that will store the information in BaseDtos.cs 

For a two player map initial map data would have stored this XY coordinate as start location 1. for a three player map initial map data would have stored start location 1 and start location 2 so each time these spawn locations are played for the first time then we generate the greedy map ordering and teams For the spawn location 

 Initial map data does not know the worker formation for the other spawn  locations  But it can find every mineral and COM. If we select the visable units belonging to self we can Determine which worker is W12 through W1 and then based on the anchor W12 thru W1 We can calculate the mineral labels and in the same manner that initial map data did for start location 0 For this new spawn location   

 Com will already be calculated  by Initial map data For the current spawn location. Visible Self units need to be observed and with their XY coordinates and New Tag Id.  Then Teams and labels are then created the exact same way that initial map data did.  BaseDtos.cs Should have a place to store this information for the new spawn location         

 OngoingMapData.cs
 We could add Boolean Settings.SpawnDataLoaded

 if !Settings.MapDataLoaded run InitialMapData 
 if !Settings.SpawnDataLoaded run SecondaryMapData
 if both are true run OngoingMapData
  If we spawn on a map and location that has already been played then we need to refresh the tag ID  For Visible minerals and units  
W12 thru W1 are Determined the same way from COM  Teams should have been assigned for instance W3 could have been T1.  Minerals and Vespine do not move they can be assigned by XY and then refreashed to get a new Tag ID.  Workers wiggle, so exact YX might fail. 