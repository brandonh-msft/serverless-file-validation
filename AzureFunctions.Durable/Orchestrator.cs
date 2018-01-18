using Gatekeeper;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;

namespace AzureFunctions.Durable
{
    public static class Orchestrator
    {
        [FunctionName("Orchestrator")]
        public static async System.Threading.Tasks.Task<HttpResponseMessage> RunAsync([HttpTrigger(AuthorizationLevel.Function, "post", Route = null)]HttpRequestMessage req, [OrchestrationClient]DurableOrchestrationClient starter, TraceWriter log)
        {
            var inputToFunction = JToken.ReadFrom(new JsonTextReader(new StreamReader(await req.Content.ReadAsStreamAsync())));
            dynamic eventGridSoleItem = (inputToFunction as JArray)?.SingleOrDefault();
            if (eventGridSoleItem == null)
            {
                return req.CreateCompatibleResponse(HttpStatusCode.BadRequest, @"Expecting only one item in the Event Grid message");
            }

            if (eventGridSoleItem.eventType == @"Microsoft.EventGrid.SubscriptionValidationEvent")
            {
                log.Verbose(@"Event Grid Validation event received.");
                return req.CreateCompatibleResponse(HttpStatusCode.OK, $"{{ \"validationResponse\" : \"{((dynamic)inputToFunction)[0].data.validationCode}\" }}");
            }

            CustomerBlobAttributes newCustomerFile = Helpers.ParseEventGridPayload(eventGridSoleItem, log);
            if (newCustomerFile == null)
            {   // The request either wasn't valid (filename couldn't be parsed) or not applicable (put in to a folder other than /inbound)
                return req.CreateCompatibleResponse(HttpStatusCode.NoContent);
            }

            string customerName = newCustomerFile.CustomerName, name = newCustomerFile.Filename, containerName = newCustomerFile.ContainerName;
            starter.Log(log, $@"Processing new file. customer: {customerName}, filename: {name}");

            // get the prefix for the name so we can check for others in the same container with in the customer blob storage account
            var prefix = newCustomerFile.BatchPrefix;

            var instanceForPrefix = await starter.GetStatusAsync(prefix);
            if (instanceForPrefix == null)
            {
                starter.Log(log, $@"New instance needed for prefix '{prefix}'. Starting...");
                var retval = await starter.StartNewAsync(@"EnsureAllFiles", prefix, eventGridSoleItem);
                starter.Log(log, $@"Started. {retval}");
            }
            else
            {
                starter.Log(log, $@"Instance already waiting. Current status: {instanceForPrefix.RuntimeStatus}. Firing 'newfile' event...");

                if (instanceForPrefix.RuntimeStatus == OrchestrationRuntimeStatus.Failed)
                {
                    await starter.TerminateAsync(prefix, @"bounce");
                    var retval = await starter.StartNewAsync(@"EnsureAllFiles", prefix, eventGridSoleItem);
                    starter.Log(log, $@"Restarted listener for {prefix}. {retval}");
                }
                else
                {
                    await starter.RaiseEventAsync(prefix, @"newfile", newCustomerFile.Filename);
                }
            }


            return starter.CreateCheckStatusResponse(req, prefix);
        }
    }
}
