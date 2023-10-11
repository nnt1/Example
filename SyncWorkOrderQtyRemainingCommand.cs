using Example.Api.Responses.ViewModels.Shifts;
using Example.Business.Extensions;
using Example.Business.Interfaces;
using Example.Business.Utilities;
using Example.Model;
using Example.Model.Enums;
using Example.Model.GreatPlain;
using Example.Model.Queries;
using Example.Persistence.Interfaces;
using Example.Persistence.Interfaces.GreatPlain;
using NodaTime;

namespace Example.Business.Commands.GreatPlain
{
    internal class SyncWorkOrderQtyRemainingCommand : Command
    {
        [Injected]
        private readonly IBlockScheduleWorkflowFactory _blockScheduleWorkflowFactory = null!;
        [Injected]
        private readonly IBlockScheduleCrudFactory _blockScheduleCrudFactory = null!;
        [Injected]
        private readonly IWorkOrderCrudFactory workOrderCrudFactory = null!;
        [Injected]
        private readonly IGPWorkOrderQuantityCrudFactory _gPWorkOrderQuantityCrudFactory = null!;
        [Injected]
        private readonly ISyncHistoryCrudFactory _syncHistoryCrudFactory = null!;
        [Injected]
        private readonly IShiftCrudFactory _shiftCrudFactory = null!;
        [Injected]
        private readonly ITransactionFactory _transactionFactory = null!; 
        private readonly int? _workOrderId;

        public SyncWorkOrderQtyRemainingCommand(IServiceProvider serviceProvider, int? workOrderId = null)
            : base(serviceProvider)
        {
            _workOrderId = workOrderId;
        }

        private async Task<IEnumerable<BlockSchedule>> GetBlockSchedulesToSync()
        {
            ICommand<IEnumerable<BlockSchedule>> command = _blockScheduleCrudFactory.GetBlockSchedules<BlockSchedule>(
                x => x.BlockScheduleType == BlockScheduleType.WorkOrder
                    && x.EndTime > DateTimeUtility.TodayInstant()
                    && x.WorkOrder != null && x.WorkOrder.FinishedManually == false // ignore work orders have been finished manually
                    && (x.WorkOrder.Id == _workOrderId || _workOrderId == null),
                x => x.OrderBy(x => x.StartTime)
            );
            await command.ExecuteAsync();

            return command.Result;
        }

        protected override async Task ExecuteInnerAsync()
        {
            List<ICommand> commands = new List<ICommand>();

            IEnumerable<BlockSchedule> blockSchedules = await GetBlockSchedulesToSync();
            IEnumerable<WorkOrder> workOrders = blockSchedules
            .Where(x => x.WorkOrder != null)
            .Select(x => x.WorkOrder!)
            .GroupBy(o => o.Id)
            .Select(g => g.First());

            string joinedWorkOrderNumbers = string.Join(",", workOrders.Select(wo => wo.WorkOrderNumber));
            IEnumerable<GPWorkOrderQtyRemaining> gpWorkOrderQtyRemainings = await GetGPWorkOrderQtyRemaining(joinedWorkOrderNumbers);

            List<BlockSchedule> blockSchedulesUpdated = new List<BlockSchedule>();
            if (gpWorkOrderQtyRemainings.Any())
            {
                foreach (var gpWorkOrderQtyRemaining in gpWorkOrderQtyRemainings)
                {
                    BlockSchedule? blockScheduleToUpdate = blockSchedules.Where(x => x.WorkOrder?.WorkOrderNumber == gpWorkOrderQtyRemaining.WorkorderNumber && x.Asset.Name == gpWorkOrderQtyRemaining.AssetName).FirstOrDefault();
                    if(blockScheduleToUpdate != null && blockScheduleToUpdate.WorkOrder != null && blockScheduleToUpdate.WorkOrder.RatePerHour != 0)
                    {
                        List<ShiftBaseViewModel> shifts = await GetShiftsBy(blockScheduleToUpdate.AssetId, blockScheduleToUpdate.StartTime);
                        
                        blockScheduleToUpdate.CalculateDurationMinutes(
                            gpWorkOrderQtyRemaining.QtyRemaining, 
                            blockScheduleToUpdate.WorkOrder.RatePerHour,
                            blockScheduleToUpdate.WorkOrder.ChangeOvertime,
                            shifts);

                        blockScheduleToUpdate.RecalculateStartTimeEndTime(blockScheduleToUpdate.StartTime, true, shifts);

                        commands.Add(_blockScheduleCrudFactory.Upsert(blockScheduleToUpdate));
                        blockSchedulesUpdated.Add(blockScheduleToUpdate);
                    }
                }

                // Calculate the sum of QtyRemaining grouped by WorkorderNumber
                var gpWOQtyRemainingsSumByWorkorderNumber = gpWorkOrderQtyRemainings
                    .GroupBy(item => item.WorkorderNumber)
                    .Select(group => new
                    {
                        WorkorderNumber = group.Key,
                        SumQtyRemaining = group.Sum(item => item.QtyRemaining)
                    })
                    .ToList();
                foreach (var workOrder in workOrders)
                {
                    var gpWOQtyRemaining = gpWOQtyRemainingsSumByWorkorderNumber.Where(x => x.WorkorderNumber == workOrder.WorkOrderNumber).FirstOrDefault();
                    if (gpWOQtyRemaining != null)
                    {
                        commands.Add(workOrderCrudFactory.UpdateWorkOrderValues(workOrder.Id, PropertyValue<WorkOrder>.Create(c => c.QtyRemaining, gpWOQtyRemaining.SumQtyRemaining)));
                    }
                }
            }

            var syncDate = DateTimeUtility.TodayInstant();
            // Process WO finished
            foreach (var wo in workOrders)
            {
                if (!gpWorkOrderQtyRemainings.Any(x=>x.WorkorderNumber == wo.WorkOrderNumber) && wo.StartTime < syncDate)
                {
                    commands.Add(_blockScheduleWorkflowFactory.FinishWorkOrder(wo.Id, syncDate, false));
                }
            }

            // Update last sync
            commands.Add(_syncHistoryCrudFactory.UpsertSyncHistory(new SyncHistory()
            {
                Type = "WorkOrderQtyRemaining",
                SyncDate = syncDate
            }));

            ICommand transaction = _transactionFactory.GetTransaction(commands);
            await transaction.ExecuteAsync();

            // Call autosorting for each asset and Update Start time, End time of WOs
            List<BlockSchedule> minStartTimeWOsPerAsset = blockSchedulesUpdated
            .GroupBy(schedule => schedule.AssetId)
            .Select(group => group.OrderBy(schedule => schedule.StartTime).First())
            .ToList();
            
            foreach (BlockSchedule blockSchedule in minStartTimeWOsPerAsset)
            {
                ICommand<IEnumerable<BlockSchedule>> autoSortingCommand = _blockScheduleWorkflowFactory.AutoSortingWorkOrders("UpdateWorkOrderQtyRemaining", 0, blockSchedule.AssetId, blockSchedule.StartTime);
                _ = autoSortingCommand.ExecuteAsync();
            }
        }

        private async Task<IEnumerable<GPWorkOrderQtyRemaining>> GetGPWorkOrderQtyRemaining(string workOrderList)
        {
            ICommand<IEnumerable<GPWorkOrderQtyRemaining>> command = _gPWorkOrderQuantityCrudFactory.GetGPWorkOrderQuantities(
                new GPWorkOrderQtyRemainingSprocParams()
                {
                    WorkOrderList = workOrderList
                });
            await command.ExecuteAsync();

            return command.Result;
        }

        private async Task<List<ShiftBaseViewModel>> GetShiftsBy(int assetId, Instant fromDateTime)
        {
            ICommand<IEnumerable<ShiftBaseViewModel>> getShiftsCommand = _shiftCrudFactory.GetShifts<ShiftBaseViewModel>(s => s.Asset.Id == assetId && s.EndDateTime > fromDateTime);
            await getShiftsCommand.ExecuteAsync();
            IEnumerable<ShiftBaseViewModel> shifts = getShiftsCommand.Result.OrderBy(x => x.StartDateTime);
            return shifts.ToList();
        }
    }
}
