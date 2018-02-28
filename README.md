# File processing and validation using Azure Functions
This sample outlines two ways to accomplish the following set of requirements using Azure Functions. One way uses the "traditional" serverless approach, and the other uses Azure Functions' new _<a href="https://docs.microsoft.com/en-us/azure/azure-functions/durable-functions-overview" target="_blank">Durable Functions</a>_ feature.
## Problem statement
Given a set of customers, assume each customer uploads data to our backend for historical record keeping and analysis. This data arrives in the form of a **set** of `.csv` files with each file containing different data. Think of them almost as SQL Table dumps in CSV format.

When the customer uploads the files, we have two primary objectives:
1. Ensure that all the files required for the customer are present for a particular "set" (aka "batch") of data
2. Only when we have all the files for a set, continue on to validate the structure of each file ensuring a handful of requirements:
    * Each file must be UTF-8 encoded
    * Depending on the file (type1, type2, etc), ensure the correct # of columns are present in the CSV file

## Setup
To accomplish this sample, you'll need to set up a few things:

1. Azure General Purpose Storage
    * For the Functions SDK to store its dashboard info, and the Durable Functions to store their state data
1. Azure Blob Storage
    * For the customer files to be uploaded in to
1. Azure Event Grid (with Storage Events)
1. ngrok to enable local Azure Function triggering from Event Grid (see <a href="https://blogs.msdn.microsoft.com/brandonh/2017/11/30/locally-debugging-an-azure-function-triggered-by-azure-event-grid/" target="_blank">this blog post</a> for more)
1. Visual Studio 2017 v15.5.4+ with the **Azure Workload** installed.
1. The *Azure Functions and Web Jobs Tools* extension to VS, version 15.0.40108+
1. <a href="https://azure.microsoft.com/en-us/features/storage-explorer/" target="_blank">Azure Storage Explorer</a> (makes testing easier)

## Execution

Pull down the code.

Create a new file in the `AzureFunctions.NetCore` **project** called `local.settings.json` with the following content:
```js
{
  "IsEncrypted": false,
  "Values": {
    "AzureWebJobsStorage": "<general purpose storage connection string>",
    "AzureWebJobsDashboard": "<general purpose storage connection string>",

    "CustomerBlobStorage": "<blob storage connection string>",
    "ValidateFunctionUrl": "http://localhost:7071/api/Validate"
  }
}
```

This file will be used across the functions, durable or otherwise.

Next, run either of the Durable Function apps in this solution. You can use the v1 (.Net Framework) or the .Net Core version, it's only needed for Event Grid validation.
With the function running, add an Event Grid Subscription to the Blob Storage account (from step 2), pointing to the ngrok-piped endpoint you created in step 4. The URL should look something like this: `https://b3252cc3.ngrok.io/api/Orchestrator`

![](https://brandonhmsdnblog.blob.core.windows.net/images/2018/01/17/s2018-01-17_14-59-32.png)

Upon saving this subscription, you'll see your locally-running Function get hit with a request and return HTTP OK, then the Subscription will go green in Azure and you're set.

Now, open Azure Storage Explorer and connect to the *Blob* Storage Account you've created. In here, create a container named `cust1`. Inside the container, create a new folder called `inbound`.

Take one of the `.csv` files from the `sampledata` folder of this repo, and drop it in to the inbound folder.

You should see your local function's `/api/Orchestrator` endpoint get hit if you're running the Durable Function.

The Durable Function execution works like this:
1. Determine the "batch prefix" of the file that was dropped. This consists of the customer name (cust1), and a datetime stamp in the format YYYYMMDD_HHMM, making the batch prefix for the first batch in `sampledata` defined as `cust1_20171010_1112`
1. Check to see if a sub-orchestration for this batch already exists.
2. If not, spin one up and pass along the Event Grid data that triggered this execution
3. If so, use `RaiseEvent` to pass the filename along to the instance.

In the `EnsureAllFiles` sub-orchestration, we look up what files we need for this customer (cust1) and check to see which files have come through thus far. As long as we do *not* have the files we need, we loop within the orchestration. Each time waiting for an external `newfile` event to be thrown to let us know a new file has come through and should be processed.

When we find we have all the files that constitute a "batch" for the customer, we call the `ValidateFileSet` activity function to process each file in the set and validate the structure of them according to our rules.

When Validation completes successfully, all files from the batch are moved to a `valid-set` subfolder in the blob storage container. If validation fails (try removing a column in one of the lines in one of the files), the whole set gets moved to `invalid-set`

### Resetting Execution
Because of the persistent behavior of state for Durable Functions, if you need to reset the execution because something goes wrong it's not as simple as just re-running the function. To do this properly, you must **delete the `DurableFunctionsHubHistory` Table** in the "General Purpose" Storage Account you created in Step 1 above.

**Note**: after doing this you'll have to wait a minute or so before running either of the Durable Function implementations as the storage table creation will error with 409 CONFLICT while deletion takes place.

If you are using the classic Functions (not the Durable ones), then to restart properly you must delete the `FileProcessingLocks` table from the General Purpose Storage Account.

In addition to either of the above steps, you'll also want to delete any files you uploaded to the `/inbound` directory of the blob storage container triggering the Functions.

## Known issues
* If you drop all the files in at once, there exists a race condition when the events fired from Event Grid hit the top-level Orchestrator endpoint; it doesn't execute `StartNewAsync` fast enough and instead of one instance per batch, you'll end up with multiple instances even though we desire them to be singletons by batch.
* If you drop one file in, then drop the remainder after the singleton has been spun up, you'll enter an infinite loop of execution. This is fixed by [a pull request](https://github.com/Azure/azure-functions-durable-extension/pull/144) which is slated to be in the beta3 release of the Durable Functions package.
* The `400 BAD REQUEST` return if errors are found in the set suffers from [this bug](https://github.com/Azure/azure-functions-host/issues/2475) on the Functions v2 runtime as of this writing.