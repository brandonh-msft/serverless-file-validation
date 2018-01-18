using Microsoft.Azure.WebJobs.Host;

namespace Gatekeeper
{
    static class DurableContextExtensions
    {
        public static void Log(this Microsoft.Azure.WebJobs.DurableOrchestrationContext context, TraceWriter log, string messsage, bool onlyIfNotReplaying = true)
        {
            if (!onlyIfNotReplaying || !context.IsReplaying)
            {
                log.Warning(messsage);
            }
        }
    }
}
