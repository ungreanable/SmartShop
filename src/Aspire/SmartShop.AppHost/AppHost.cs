// Local development orchestration: `dotnet run --project src/Aspire/SmartShop.AppHost`
// Starts PostgreSQL, Valkey, RabbitMQ, SeaweedFS (S3) and every SmartShop service with an Aspire dashboard.
var builder = DistributedApplication.CreateBuilder(args);

var postgres = builder.AddPostgres("postgres")
    .WithDataVolume("smartshop-postgres")
    .WithLifetime(ContainerLifetime.Persistent);
var database = postgres.AddDatabase("smartshop");

var cache = builder.AddValkey("cache")
    .WithLifetime(ContainerLifetime.Persistent);

var rabbitmq = builder.AddRabbitMQ("rabbitmq")
    .WithManagementPlugin()
    .WithLifetime(ContainerLifetime.Persistent);

var seaweed = builder.AddContainer("seaweedfs", "chrislusf/seaweedfs", "3.97")
    .WithArgs("server", "-s3", "-dir=/data", "-master.volumeSizeLimitMB=256")
    .WithHttpEndpoint(targetPort: 8333, name: "s3")
    .WithVolume("smartshop-seaweedfs", "/data")
    .WithLifetime(ContainerLifetime.Persistent);

var s3 = seaweed.GetEndpoint("s3");

var api = builder.AddProject<Projects.SmartShop_Api>("api")
    .WithReference(database).WaitFor(database)
    .WithReference(cache).WaitFor(cache)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WaitFor(seaweed)
    .WithEnvironment("Storage__Provider", "S3")
    .WithEnvironment("Storage__ServiceUrl", s3)
    .WithEnvironment("Storage__AccessKey", "smartshop")
    .WithEnvironment("Storage__SecretKey", "smartshop")
    .WithEnvironment("App__PublicUrl", "http://localhost:5100");

builder.AddProject<Projects.SmartShop_Worker>("worker")
    .WithReference(database).WaitFor(database)
    .WithReference(cache).WaitFor(cache)
    .WithReference(rabbitmq).WaitFor(rabbitmq)
    .WaitFor(api) // the API applies migrations on start in development
    .WithEnvironment("Storage__Provider", "S3")
    .WithEnvironment("Storage__ServiceUrl", s3)
    .WithEnvironment("Storage__AccessKey", "smartshop")
    .WithEnvironment("Storage__SecretKey", "smartshop");

builder.AddProject<Projects.SmartShop_Web>("web")
    .WithReference(api).WaitFor(api)
    .WithExternalHttpEndpoints();

builder.Build().Run();
