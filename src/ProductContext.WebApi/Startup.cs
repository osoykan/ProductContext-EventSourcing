﻿using AggregateSource;
using AggregateSource.EventStore;
using AggregateSource.EventStore.Resolvers;
using AggregateSource.EventStore.Snapshots;
using Couchbase.Core;
using EventStore.ClientAPI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NodaTime;
using ProductContext.Domain.Contracts;
using ProductContext.Domain.Products;
using ProductContext.Domain.Projections;
using ProductContext.Framework;
using ProductContext.WebApi.Plumbing;
using Swashbuckle.AspNetCore.Swagger;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace ProductContext.WebApi
{
    public class Startup
    {
        private static readonly GetSnapshotStreamName getSnapshotStreamName = (type, id) => $"{getStreamName(type, id)}-Snapshot";
        private static readonly GetStreamName getStreamName = (type, id) => $"{type.Name}-{id}";
        private static readonly Now now = () => SystemClock.Instance.GetCurrentInstant().ToDateTimeUtc();

        public Startup(IHostingEnvironment env)
        {
            IConfigurationBuilder builder = new ConfigurationBuilder()
                .SetBasePath(env.ContentRootPath)
                .AddJsonFile("appsettings.json", false, true)
                .AddJsonFile($"appsettings.{env.EnvironmentName}.json", true)
                .AddEnvironmentVariables();

            Configuration = builder.Build();
        }

        private IConfiguration Configuration { get; }

        public void ConfigureServices(IServiceCollection services) => ConfigureServicesAsync(services).GetAwaiter().GetResult();

        private async Task ConfigureServicesAsync(IServiceCollection services)
        {
            services.AddMvc();

            services.AddSwaggerGen(c => { c.SwaggerDoc("v1", new Info { Title = "Event Sourced Product Creation", Version = "v1" }); });

            IEventStoreConnection esConnection = Defaults.GetEsConnection(
                Configuration["Data:EventStore:Username"],
                Configuration["Data:EventStore:Password"],
                Configuration["Data:EventStore:Url"]).GetAwaiter().GetResult();

            services.AddTransient<IBus>(_ =>
            {
                var bus = new InMemoryBus("bus");
                var defaultSerializer = new DefaultEventDeserializer();
                var defaultSnapshotDeserializer = new DefaultSnapshotDeserializer();
                var concurrentUnitOfWork = new ConcurrentUnitOfWork();

                var productRepository = new AsyncRepository<Product>(
                    Product.Factory,
                    concurrentUnitOfWork,
                    esConnection,
                    new EventReaderConfiguration(new SliceSize(500), defaultSerializer, new TypedStreamNameResolver(typeof(Product), getStreamName), new NoStreamUserCredentialsResolver()));

                var productSnapshotableRepository = new AsyncSnapshotableRepository<Product>(
                    Product.Factory,
                    concurrentUnitOfWork,
                    esConnection,
                    new EventReaderConfiguration(new SliceSize(500), defaultSerializer, new TypedStreamNameResolver(typeof(Product), getStreamName), new NoStreamUserCredentialsResolver()),
                    new AsyncSnapshotReader(esConnection, new SnapshotReaderConfiguration(defaultSnapshotDeserializer, new SnapshotableStreamNameResolver(typeof(Product), getStreamName), new NoStreamUserCredentialsResolver())));

                var productCommandHandlers = new ProductCommandHandlers(
                    getStreamName,
                    getSnapshotStreamName,
                    productRepository,
                    productSnapshotableRepository,
                    now);

                bus.Subscribe<Commands.V1.CreateProduct>(productCommandHandlers);
                bus.Subscribe<Commands.V1.AddVariantToProduct>(productCommandHandlers);
                bus.Subscribe<Commands.V1.AddContentToProduct>(productCommandHandlers);

                return bus;
            });

            InitProjections().Wait();
        }

        public void Configure(IApplicationBuilder app, IHostingEnvironment env)
        {
            if (env.IsDevelopment() || env.IsLocal())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseSwagger()
               .UseSwaggerUI(c => { c.SwaggerEndpoint("/swagger/v1/swagger.json", "My API V1"); })
               .UseMvc();
        }

        private async Task InitProjections()
        {
            IEventStoreConnection esConnection = await Defaults.GetEsConnection(
                Configuration["Data:EventStore:Username"],
                Configuration["Data:EventStore:Password"],
                Configuration["Data:EventStore:Url"]);

            Func<IBucket> getBucket = Defaults.GetCouchbaseBucket(nameof(ProductContext),
                Configuration["Data:Couchbase:Username"],
                Configuration["Data:Couchbase:Password"],
                Configuration["Data:Couchbase:Url"]);

            Func<string, Task<Aggregate>> getProductAggregate = async streamId =>
            {
                var defaultSerializer = new DefaultEventDeserializer();
                var concurrentUnitOfWork = new ConcurrentUnitOfWork();

                var productRepository = new AsyncRepository<Product>(
                    Product.Factory,
                    concurrentUnitOfWork,
                    esConnection,
                    new EventReaderConfiguration(
                        new SliceSize(500),
                        defaultSerializer,
                        new TypedStreamNameResolver(typeof(Product), getStreamName),
                        new NoStreamUserCredentialsResolver()));

                await productRepository.GetAsync(streamId);

                return concurrentUnitOfWork.GetChanges().First();
            };

            await ProjectionManagerBuilder.With
                                          .Connection(esConnection)
                                          .Deserializer(new DefaultEventDeserializer())
                                          .CheckpointStore(new CouchbaseCheckpointStore(getBucket))
                                          .Snaphotter(
                                              new EventStoreSnapshotter<Aggregate, ProductSnapshot>(
                                                  getProductAggregate,
                                                  () => esConnection,
                                                  e => e.Event.EventNumber > 0 && e.Event.EventNumber % 5 == 0,
                                                  stream => $"{stream}-Snapshot",
                                                 now))
                                          .Projections(
                                              ProjectorDefiner.For<ProductProjection>()
                ).Activate(getBucket);
        }
    }
}
