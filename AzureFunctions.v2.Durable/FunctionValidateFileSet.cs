using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Extensions.Logging;
using Microsoft.WindowsAzure.Storage;

namespace FileValidation
{
    public static class FunctionValidateFileSet
    {
        [FunctionName(@"ValidateFileSet")]
        public static async Task<bool> Run([ActivityTrigger]DurableActivityContext context, ILogger log)
        {
            log.LogTrace(@"ValidateFileSet run.");
            if (!CloudStorageAccount.TryParse(Environment.GetEnvironmentVariable(@"CustomerBlobStorage"), out _))
            {
                throw new Exception(@"Can't create a storage account accessor from app setting connection string, sorry!");
            }

            var payload = context.GetInputAsJson();
            var prefix = payload["prefix"].ToString(); // This is the entire path w/ prefix for the file set

            return await Helpers.DoValidationAsync(prefix, log);
        }
    }
}
