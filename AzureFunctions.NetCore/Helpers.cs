using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System;

namespace AzureFunctions
{
    static class Helpers
    {
        public static async System.Threading.Tasks.Task<CloudTable> GetLockTableAsync(CloudStorageAccount storageAccount = null)
        {
            CloudTable bottlerFilesTable;
            if (storageAccount == null)
            {
                if (!CloudStorageAccount.TryParse(Environment.GetEnvironmentVariable(@"AzureWebJobsStorage"), out var sa))
                {
                    throw new Exception(@"Can't create a storage account accessor from app setting connection string, sorry!");
                }
                else
                {
                    storageAccount = sa;
                }
            }

            try
            {
                bottlerFilesTable = storageAccount.CreateCloudTableClient().GetTableReference(@"FileProcessingLocks");
            }
            catch (Exception ex)
            {
                throw new Exception($@"Error creating table client for locks: {ex}", ex);
            }

            await bottlerFilesTable.CreateIfNotExistsAsync();

            return bottlerFilesTable;
        }

        public static CustomerBlobAttributes ParseEventGridPayload(dynamic eventGridItem, TraceWriter log)
        {
            if (eventGridItem.eventType == @"Microsoft.Storage.BlobCreated"
                && eventGridItem.data.api == @"PutBlob"
                && eventGridItem.data.contentType == @"text/csv")
            {
                try
                {
                    var retVal = CustomerBlobAttributes.Parse((string)eventGridItem.data.url);
                    if (retVal != null && !retVal.ContainerName.Equals(retVal.CustomerName))
                    {
                        throw new ArgumentException($@"File '{retVal.Filename}' uploaded to container '{retVal.ContainerName}' doesn't have the right prefix: the first token in the filename ({retVal.CustomerName}) must be the customer name, which should match the container name", nameof(eventGridItem));
                    }

                    return retVal;
                }
                catch (Exception ex)
                {
                    log.Error(@"Error parsing Event Grid payload", ex);
                }
            }

            return null;
        }

    }
}
