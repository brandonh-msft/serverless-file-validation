using AzureFunctions;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Host;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Gatekeeper
{
    public static class FunctionEnsureAllFiles
    {
        [FunctionName("EnsureAllFiles")]
        public static async Task Run([OrchestrationTrigger]DurableOrchestrationContext context, TraceWriter log)
        {
            if (!context.IsReplaying)
            {
                context.LogInfo(log, $@"EnsureAllFiles STARTING - InstanceId: {context.InstanceId}");
            }
            else
            {
                context.LogInfo(log, $@"EnsureAllFiles REPLAYING");
            }

            dynamic eventGridSoleItem = context.GetInputAsJson();

            CustomerBlobAttributes newCustomerFile = Helpers.ParseEventGridPayload(eventGridSoleItem, log);
            if (newCustomerFile == null)
            {   // The request either wasn't valid (filename couldn't be parsed) or not applicable (put in to a folder other than /inbound)
                return;
            }

            var expectedFiles = new HashSet<string>(await context.CallActivityAsync<IEnumerable<string>>(nameof(GetExpectedFilesForCustomer), newCustomerFile.CustomerName));
            var filename = newCustomerFile.Filename;

            while (expectedFiles.Any())
            {
                expectedFiles.Remove(Path.GetFileNameWithoutExtension(filename).Split('_').Last());
                if (expectedFiles.Count == 0) continue;

                context.LogInfo(log, $@"Still waiting for more files... Still need {string.Join(", ", expectedFiles)} for customer {newCustomerFile.CustomerName}, batch {newCustomerFile.BatchPrefix}");

                filename = await context.WaitForExternalEvent<string>(@"newfile");
                context.LogInfo(log, $@"Got new file via event: {filename}");
            }

            // Verify that this prefix isn't already in the lock table for processings
            context.LogInfo(log, @"Got all the files! Moving on...");

            using (var c = new HttpClient())
            {
                var jsonObjectForValidator =
$@"{{
    ""prefix"" : ""{newCustomerFile.ContainerName}/inbound/{newCustomerFile.BatchPrefix}"",
    ""fileTypes"" : [
        {string.Join(", ", expectedFiles.Select(e => $@"""{e}"""))}
    ]
}}";

                // call next step in functions with the prefix so it knows what to go grab
                await context.CallActivityAsync(@"ValidateFileSet", jsonObjectForValidator);
            }
        }

        [FunctionName(@"GetExpectedFilesForCustomer")]
        public static IEnumerable<string> GetExpectedFilesForCustomer([ActivityTrigger]DurableActivityContext context, TraceWriter log) => new[] { @"file1", @"file2", @"file3", @"file4", @"file5", @"file6" };

        class BlobFilenameVsDatabaseFileMaskComparer : IEqualityComparer<string>
        {
            public bool Equals(string x, string y) => y.Contains(x);

            public int GetHashCode(string obj) => obj.GetHashCode();
        }
    }
}
