﻿using AzureFunctions;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Blob;
using Microsoft.WindowsAzure.Storage.Table;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Gatekeeper
{
    public static class FunctionValidateFileSet
    {
        [FunctionName(@"ValidateFileSet")]
        public static async Task<bool> Run([ActivityTrigger]DurableActivityContext context, TraceWriter log)
        {
            log.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Verbose, @"ValidateFileSet run."));
            if (!CloudStorageAccount.TryParse(Environment.GetEnvironmentVariable(@"CustomerBlobStorage"), out var storageAccount))
            {
                throw new Exception(@"Can't create a storage account accessor from app setting connection string, sorry!");
            }

            var payload = context.GetInputAsJson();
            var prefix = payload["prefix"].ToString(); // This is the entire path w/ prefix for the file set
            log.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Verbose, $@"prefix: {prefix}"));

            var filePrefix = prefix.Substring(prefix.LastIndexOf('/') + 1);
            log.Trace(new TraceEvent(System.Diagnostics.TraceLevel.Verbose, $@"filePrefix: {filePrefix}"));

            var blobClient = storageAccount.CreateCloudBlobClient();
            var targetBlobs = await blobClient.ListBlobsAsync(WebUtility.UrlDecode(prefix));

            var customerName = filePrefix.Split('_').First().Split('-').Last();

            var errors = new List<string>();
            var filesToProcess = payload["fileTypes"].Values<string>();

            foreach (var blobDetails in targetBlobs)
            {
                var blob = await blobClient.GetBlobReferenceFromServerAsync(blobDetails.StorageUri.PrimaryUri);

                var fileParts = CustomerBlobAttributes.Parse(blob.Uri.AbsolutePath);
                if (!filesToProcess.Contains(fileParts.Filetype, StringComparer.OrdinalIgnoreCase))
                {
                    log.Verbose($@"{blob.Name} skipped. Isn't in the list of file types to process ({string.Join(", ", filesToProcess)}) for bottler '{customerName}'");
                    continue;
                }

                var lowerFileType = fileParts.Filetype.ToLowerInvariant();
                uint numColumns = 0;
                switch (lowerFileType)
                {
                    case @"type5":  // salestype
                        numColumns = 2;
                        break;
                    case @"type10": // mixedpack
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
                log.Error($@"Errors found in batch {filePrefix}: {string.Join(@", ", errors)}");

                // move files to 'invalid-set' folder
                await MoveBlobsAsync(log, blobClient, targetBlobs, @"invalid-set");
                return false;
            }
            else
            {
                // move these files to 'valid-set' folder
                await MoveBlobsAsync(log, blobClient, targetBlobs, @"valid-set");

                log.Info($@"Set {filePrefix} successfully validated and queued for further processing.");
                return true;
            }
        }

        private static async Task<bool> ShouldProceedAsync(CloudTable bottlerFilesTable, string prefix, string filePrefix, TraceWriter log)
        {
            try
            {
                var lockRecord = await LockTableEntity.GetLockRecordAsync(filePrefix, bottlerFilesTable);
                if (lockRecord?.State == LockTableEntity.BatchState.Waiting)
                {
                    // Update the lock record to mark it as in progress
                    lockRecord.State = LockTableEntity.BatchState.InProgress;
                    await bottlerFilesTable.ExecuteAsync(TableOperation.Replace(lockRecord));
                    return true;
                }
                else
                {
                    log.Info($@"Validate for {prefix} skipped. State was {lockRecord?.State.ToString() ?? @"[null]"}.");
                }
            }
            catch (StorageException)
            {
                log.Info($@"Validate for {prefix} skipped (StorageException. Somebody else picked it up already.");
            }

            return false;
        }

        private static async Task MoveBlobsAsync(TraceWriter log, CloudBlobClient blobClient, IEnumerable<IListBlobItem> targetBlobs, string folderName)
        {
            foreach (var b in targetBlobs)
            {
                var blobRef = await blobClient.GetBlobReferenceFromServerAsync(b.StorageUri.PrimaryUri);
                var sourceBlob = b.Container.GetBlockBlobReference(blobRef.Name);

                var targetBlob = blobRef.Container
                    .GetDirectoryReference($@"{folderName}")
                    .GetBlockBlobReference(Path.GetFileName(blobRef.Name));

                await targetBlob.StartCopyAsync(sourceBlob);

                while (targetBlob.CopyState.Status == CopyStatus.Pending) ;     // spinlock until the copy completes

                bool copySucceeded = targetBlob.CopyState.Status == CopyStatus.Success;
                if (!copySucceeded)
                {
                    log.Error($@"Error copying {sourceBlob.Name} to {folderName} folder. Retrying once...");

                    await targetBlob.StartCopyAsync(sourceBlob);

                    while (targetBlob.CopyState.Status == CopyStatus.Pending) ;     // spinlock until the copy completes

                    copySucceeded = targetBlob.CopyState.Status == CopyStatus.Success;
                    if (!copySucceeded)
                    {
                        log.Error($@"Error retrying copy of {sourceBlob.Name} to {folderName} folder. File not moved.");
                    }
                }

                if (copySucceeded)
                {
#if DEBUG
                    try
                    {
#endif
                        await sourceBlob.DeleteAsync();
#if DEBUG
                    }
                    catch (StorageException ex)
                    {
                        log.Error($@"Error deleting blob {sourceBlob.Name}", ex);
                    }
#endif
                }
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
    }
}
