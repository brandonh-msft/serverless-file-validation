using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace FileValidation
{
    [JsonObject(MemberSerialization.OptIn)]
    public class BatchEntity : IBatchEntity
    {
        private readonly string _id;
        private readonly ILogger _logger;
        private static readonly string[] ExpectedFilesForCustomer = new[] { @"type1", @"type2", @"type3", @"type4", @"type5", @"type7", @"type8", @"type9", @"type10" };

        public BatchEntity(string id, ILogger logger)
        {
            _id = id;
            _logger = logger;
        }

        [JsonProperty]
        public List<string> ReceivedFileTypes { get; set; } = new List<string>();

        [FunctionName(nameof(BatchEntity))]
        public static Task Run([EntityTrigger]IDurableEntityContext ctx, ILogger logger) => ctx.DispatchAsync<BatchEntity>(ctx.EntityKey, logger);

        public async Task NewFile(string fileUri)
        {
            var newCustomerFile = CustomerBlobAttributes.Parse(fileUri);
            _logger.LogInformation($@"Got new file via event: {newCustomerFile.Filename}");
            this.ReceivedFileTypes.Add(newCustomerFile.Filetype);

            _logger.LogTrace($@"Actor '{_id}' got file '{newCustomerFile.Filetype}'");

            var filesStillWaitingFor = ExpectedFilesForCustomer.Except(this.ReceivedFileTypes);
            if (filesStillWaitingFor.Any())
            {
                _logger.LogInformation($@"Still waiting for more files... Still need {string.Join(", ", filesStillWaitingFor)} for customer {newCustomerFile.CustomerName}, batch {newCustomerFile.BatchPrefix}");
            }
            else
            {
                _logger.LogInformation(@"Got all the files! Moving on...");

                // call next step in functions with the prefix so it knows what to go grab
                await ValidateFileSet($@"{newCustomerFile.ContainerName}/inbound/{newCustomerFile.BatchPrefix}");
            }
        }

        public async Task<bool> ValidateFileSet(string prefix)
        {
            _logger.LogTrace(@"ValidateFileSet run.");
            if (!CloudStorageAccount.TryParse(Environment.GetEnvironmentVariable(@"CustomerBlobStorage"), out var storageAccount))
            {
                throw new Exception(@"Can't create a storage account accessor from app setting connection string, sorry!");
            }

            _logger.LogTrace($@"prefix: {prefix}");

            var filePrefix = prefix.Substring(prefix.LastIndexOf('/') + 1);
            _logger.LogTrace($@"filePrefix: {filePrefix}");

            var blobClient = storageAccount.CreateCloudBlobClient();
            var targetBlobs = await blobClient.ListBlobsAsync(WebUtility.UrlDecode(prefix));

            var customerName = filePrefix.Split('_').First().Split('-').Last();

            var errors = new List<string>();

            foreach (var blobDetails in targetBlobs)
            {
                var blob = await blobClient.GetBlobReferenceFromServerAsync(blobDetails.StorageUri.PrimaryUri);

                var fileParts = CustomerBlobAttributes.Parse(blob.Uri.AbsolutePath);
                if (!ExpectedFilesForCustomer.Contains(fileParts.Filetype, StringComparer.OrdinalIgnoreCase))
                {
                    _logger.LogTrace($@"{blob.Name} skipped. Isn't in the list of file types to process ({string.Join(", ", ExpectedFilesForCustomer)}) for customer '{customerName}'");
                    continue;
                }

                var lowerFileType = fileParts.Filetype.ToLowerInvariant();
                uint numColumns = 0;
                switch (lowerFileType)
                {
                    case @"type5":  // salestype
                        numColumns = 2;
                        break;
                    case @"type10": // mixed
                    case @"type4":  // shipfrom
                        numColumns = 3;
                        break;
                    case @"type1":  // channel
                    case @"type2":  // customer
                        numColumns = 4;
                        break;
                    case @"type9":  // itemdetail
                        numColumns = 5;
                        break;
                    case @"type3": // shipto
                        numColumns = 14;
                        break;
                    case @"type6": // salesdetail
                        numColumns = 15;
                        break;
                    case @"type8":  // product
                        numColumns = 21;
                        break;
                    case @"type7":  // sales
                        numColumns = 23;
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(prefix), $@"Unhandled file type: {fileParts.Filetype}");
                }

                errors.AddRange(await ValidateCsvStructureAsync(blob, numColumns, lowerFileType));
            }

            if (errors.Any())
            {
                _logger.LogError($@"Errors found in batch {filePrefix}: {string.Join(@", ", errors)}");

                // move files to 'invalid-set' folder
                await MoveBlobsAsync(blobClient, targetBlobs, @"invalid-set");
                return false;
            }
            else
            {
                // move these files to 'valid-set' folder
                await MoveBlobsAsync(blobClient, targetBlobs, @"valid-set");

                _logger.LogInformation($@"Set {filePrefix} successfully validated and queued for further processing.");
                return true;
            }
        }

        private static async Task<IEnumerable<string>> ValidateCsvStructureAsync(ICloudBlob blob, uint requiredNumberOfColumnsPerLine, string filetypeDescription)
        {
            var errs = new List<string>();
            try
            {
                using (var blobReader = new StreamReader(await blob.OpenReadAsync(new AccessCondition(), new BlobRequestOptions(), new OperationContext())))
                {
                    var fileAttributes = CustomerBlobAttributes.Parse(blob.Uri.AbsolutePath);

                    for (int lineNumber = 0; !blobReader.EndOfStream; lineNumber++)
                    {
                        var errorPrefix = $@"{filetypeDescription} file '{fileAttributes.Filename}' Record {lineNumber}";
                        var line = blobReader.ReadLine();
                        var fields = line.Split(',');
                        if (fields.Length != requiredNumberOfColumnsPerLine)
                        {
                            errs.Add($@"{errorPrefix} is malformed. Should have {requiredNumberOfColumnsPerLine} values; has {fields.Length}");
                            continue;
                        }

                        for (int i = 0; i < fields.Length; i++)
                        {
                            errorPrefix = $@"{errorPrefix} Field {i}";
                            var field = fields[i];
                            // each field must be enclosed in double quotes
                            if (field[0] != '"' || field.Last() != '"')
                            {
                                errs.Add($@"{errorPrefix}: value ({field}) is not enclosed in double quotes ("")");
                                continue;
                            }
                        }
                    }

                    // Validate file is UTF-8 encoded
                    if (!blobReader.CurrentEncoding.BodyName.Equals("utf-8", StringComparison.OrdinalIgnoreCase))
                    {
                        errs.Add($@"{blob.Name} is not UTF-8 encoded");
                    }
                }
            }
            catch (StorageException storEx)
            {
                SwallowStorage404(storEx);
            }
            return errs;
        }

        private static void SwallowStorage404(StorageException storEx)
        {
            var webEx = storEx.InnerException as WebException;
            if ((webEx.Response as HttpWebResponse)?.StatusCode == HttpStatusCode.NotFound)
            {
                // Ignore
            }
            else throw storEx;
        }

        private async Task MoveBlobsAsync(CloudBlobClient blobClient, IEnumerable<IListBlobItem> targetBlobs, string folderName)
        {
            foreach (var b in targetBlobs)
            {
                var blobRef = await blobClient.GetBlobReferenceFromServerAsync(b.StorageUri.PrimaryUri);
                var sourceBlob = b.Container.GetBlockBlobReference(blobRef.Name);

                var targetBlob = blobRef.Container
                    .GetDirectoryReference($@"{folderName}")
                    .GetBlockBlobReference(Path.GetFileName(blobRef.Name));

                string sourceLeaseGuid = Guid.NewGuid().ToString(), targetLeaseGuid = Guid.NewGuid().ToString();
                var sourceLeaseId = await sourceBlob.AcquireLeaseAsync(TimeSpan.FromSeconds(60), sourceLeaseGuid);

                await targetBlob.StartCopyAsync(sourceBlob);

                while (targetBlob.CopyState.Status == CopyStatus.Pending) ;     // spinlock until the copy completes

                bool copySucceeded = targetBlob.CopyState.Status == CopyStatus.Success;
                if (!copySucceeded)
                {
                    _logger.LogError($@"Error copying {sourceBlob.Name} to {folderName} folder. Retrying once...");

                    await targetBlob.StartCopyAsync(sourceBlob);

                    while (targetBlob.CopyState.Status == CopyStatus.Pending) ;     // spinlock until the copy completes

                    copySucceeded = targetBlob.CopyState.Status == CopyStatus.Success;
                    if (!copySucceeded)
                    {
                        _logger.LogError($@"Error retrying copy of {sourceBlob.Name} to {folderName} folder. File not moved.");
                    }
                }

                if (copySucceeded)
                {
#if DEBUG
                    try
                    {
#endif
                        await sourceBlob.ReleaseLeaseAsync(new AccessCondition { LeaseId = sourceLeaseId });
                        await sourceBlob.DeleteAsync();
#if DEBUG
                    }
                    catch (StorageException ex)
                    {
                        _logger.LogError($@"Error deleting blob {sourceBlob.Name}", ex);
                    }
#endif

                }
            }
        }
    }

    public interface IBatchEntity
    {
        Task NewFile(string fileUri);
    }

}
