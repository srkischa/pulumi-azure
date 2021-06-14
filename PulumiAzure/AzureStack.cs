using Pulumi;
using Pulumi.Azure.Core;
using Pulumi.AzureNative.Insights;
using Pulumi.AzureNative.Storage;
using Pulumi.AzureNative.Web;
using Pulumi.AzureNative.Web.Inputs;
using System.IO;
using Kind = Pulumi.AzureNative.Storage.Kind;

class AzureStack : Stack
{
    public static string FunctionAppPublishFolder => Path.Combine("../", "PulumiAzure.Functions", "bin", "Release", "netcoreapp3.1");

    public AzureStack()
    {
        //var stack = Pulumi.Deployment.Instance.StackName.ToLower();

        var appName = "covid-scraper";

        var resourceGroup = new ResourceGroup($"rg-{appName}");
        var resourceGroupName = resourceGroup.Name;
        var resourceGroupLocation = resourceGroup.Location;

        var appServicePlan = new Pulumi.AzureNative.Web.AppServicePlan($"{appName}-azure-service-plan",
            new Pulumi.AzureNative.Web.AppServicePlanArgs
            {
                Kind = "FunctionApp",
                ResourceGroupName = resourceGroupName,
                Location = resourceGroupLocation,
                Sku = new SkuDescriptionArgs
                {
                    Name = "Y1",
                    Tier = "Dynamic"
                },
                Tags =
                {
                    {"environment", "dev"}
                }
            });

        var storageAccount = new StorageAccount($"storage", new StorageAccountArgs
        {
            ResourceGroupName = resourceGroupName,
            Location = resourceGroupLocation,
            Sku = new Pulumi.AzureNative.Storage.Inputs.SkuArgs
            {
                Name = SkuName.Standard_LRS,
            },
            Kind = Kind.StorageV2
        });

        var container = new BlobContainer($"zips-container", new BlobContainerArgs
        {
            AccountName = storageAccount.Name,
            PublicAccess = PublicAccess.None,
            ResourceGroupName = resourceGroup.Name
        });

        var blob = new Blob($"my-functions", new BlobArgs
        {
            AccountName = storageAccount.Name,
            ContainerName = container.Name,
            ResourceGroupName = resourceGroupName,
            Source = new FileArchive(FunctionAppPublishFolder),
            Type = BlobType.Block
        });

        var appInsights = new Component($"appInsights", new ComponentArgs
        {
            ApplicationType = ApplicationType.Web,
            Kind = "web",
            ResourceGroupName = resourceGroupName,
            Location = resourceGroupLocation
        });

        var codeBlobUrl = SignedBlobReadUrl(blob, container, storageAccount.Name, resourceGroupName);
        var storageAccountConnectionString = GetConnectionString(resourceGroupName, storageAccount.Name);

        var app = new WebApp($"{appName}", new WebAppArgs
        {
            Kind = "FunctionApp",
            ResourceGroupName = resourceGroupName,
            Location = resourceGroupLocation,
            ServerFarmId = appServicePlan.Id,
            SiteConfig = new SiteConfigArgs
            {
                AppSettings = new[]
                {
                        new NameValuePairArgs
                        {
                            Name = "APPLICATIONINSIGHTS_CONNECTION_STRING",
                            Value = Output.Format($"InstrumentationKey={appInsights.InstrumentationKey}"),
                        },
                        new NameValuePairArgs
                        {
                            Name = "FUNCTIONS_EXTENSION_VERSION",
                            Value = "~3"
                        },
                        new NameValuePairArgs
                        {
                            Name = "FUNCTIONS_WORKER_RUNTIME",
                            Value = "dotnet"
                        },
                        new NameValuePairArgs{
                            Name = "WEBSITE_RUN_FROM_PACKAGE",
                            Value = codeBlobUrl,
                        },
                        new NameValuePairArgs
                        {
                            Name = "AzureWebJobsStorage",
                            Value = storageAccountConnectionString
                        }
                    }
            }
        });


        // Export the connection string for the storage account
        ConnectionString = storageAccountConnectionString;
        Endpoint = Output.Format($"https://{app.DefaultHostName}/api/");
        FunctionName = Output.Format($"{app.Name}");
    }

    private static Output<string> GetConnectionString(Input<string> resourceGroupName, Input<string> accountName)
    {
        var accountKeys = Output.Tuple(resourceGroupName, accountName)
            .Apply(t => {
                (string rgName, string account) = t;
                return ListStorageAccountKeys.InvokeAsync(new ListStorageAccountKeysArgs { ResourceGroupName = rgName, AccountName = account });
            });

        return Output.Format($"DefaultEndpointsProtocol=https;AccountName={accountName};AccountKey={accountKeys.Apply(a => a.Keys[0].Value)};EndpointSuffix=core.windows.net");
    }

    private static Output<string> SignedBlobReadUrl(Blob blob, BlobContainer container, Output<string> account, Output<string> resourceGroupName)
    {
        return Output.Tuple(
            blob.Name, container.Name, account, resourceGroupName).Apply(t =>
            {
                (string blobName, string containerName, string accountName, string rgName) = t;

                var blobSAS = ListStorageAccountServiceSAS.InvokeAsync(new ListStorageAccountServiceSASArgs
                {
                    AccountName = accountName,
                    Protocols = HttpProtocol.Https,
                    SharedAccessStartTime = "2021-01-01",
                    SharedAccessExpiryTime = "2030-01-01",
                    Resource = SignedResource.C,
                    ResourceGroupName = rgName,
                    Permissions = Permissions.R,
                    CanonicalizedResource = "/blob/" + accountName + "/" + containerName,
                    ContentType = "application/json",
                    CacheControl = "max-age=5",
                    ContentDisposition = "inline",
                    ContentEncoding = "deflate"
                });
                return Output.Format(
                    $"https://{accountName}.blob.core.windows.net/{containerName}/{blobName}?{blobSAS.Result.ServiceSasToken}");
            });
    }

    [Output]
    public Output<string> ConnectionString { get; set; }
    
    [Output] 
    public Output<string> Endpoint { get; set; }
    
    [Output] 
    public Output<string> FunctionName { get; set; }
}
