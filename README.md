# Example
- This code using internal framework which execute commands.
- GetBlockSchedulesToSync() will be executed first, then ExecuteInnerAsync() is main logic will be executed.

This command to load current WorkOrder in system and then request to external database to get Remaining Quantity of that WorkOrder, and do these things:
- Save Remaining Quantity to database
- A WorkOrder can have multiple block on multiple asset, need group before calculate
- Re-calculate the time need to done that Remaining Quantity.
- Update sync time to database
- Group block by asset, get the first block on that asset 
and Re-calculate the start time of sequence WorkOrder because the current WorkOrder is extend/reduce after got Remaining Quantity.
