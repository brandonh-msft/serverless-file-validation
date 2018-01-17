using AzureFunctions;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace Gatekeeper
{
    class LockTableEntity : TableEntity
    {
        public LockTableEntity() : base() { }

        public LockTableEntity(string prefix) : base(prefix, prefix) { }

        [IgnoreProperty]
        public string Prefix
        {
            get { return this.PartitionKey; }
            set
            {
                this.PartitionKey = value;
                this.RowKey = value;
            }
        }

        [IgnoreProperty]
        public BatchState State { get; set; } = BatchState.Waiting;

        public string DbState
        {
            get => this.State.ToString();
            set
            {
                this.State = (BatchState)Enum.Parse(typeof(BatchState), value);
            }
        }

        public enum BatchState
        {
            Waiting, InProgress, Done
        }

        public static async Task<LockTableEntity> GetLockRecordAsync(string filePrefix, CloudTable bottlerFilesTable = null, CloudStorageAccount bottlerFilesTableStorageAccount = null)
        {
            bottlerFilesTable = bottlerFilesTable ?? await Helpers.GetLockTableAsync(bottlerFilesTableStorageAccount);

            return (await bottlerFilesTable.ExecuteQueryAsync(
                new TableQuery<LockTableEntity>()
                    .Where(TableQuery.GenerateFilterCondition(@"PartitionKey", QueryComparisons.Equal, filePrefix))))
                .SingleOrDefault();
        }

        public static async Task UpdateAsync(string filePrefix, BatchState state, CloudTable bottlerFilesTable = null, CloudStorageAccount bottlerFilesTableStorageAccount = null)
        {
            var entity = await GetLockRecordAsync(filePrefix, bottlerFilesTable);
            entity.State = state;

            bottlerFilesTable = bottlerFilesTable ?? await Helpers.GetLockTableAsync(bottlerFilesTableStorageAccount);

            await bottlerFilesTable.ExecuteAsync(TableOperation.Replace(entity));
        }
    }
}
