using System;
using System.Threading.Tasks;

using EventStore.ClientAPI;
using EventStore.ClientAPI.SystemData;

namespace AggregateSource.EventStore.Snapshots
{
    /// <summary>
    ///     Represents the default async behavior that reads a <see cref="Snapshot" /> from the underlying storage.
    /// </summary>
    public class AsyncSnapshotReader : IAsyncSnapshotReader
    {
        /// <summary>
        ///     Initializes a new instance of the <see cref="AsyncSnapshotReader" /> class.
        /// </summary>
        /// <param name="connection">The event store connection to use.</param>
        /// <param name="configuration">The configuration to use.</param>
        /// <exception cref="System.ArgumentNullException">
        ///     Thrown when <paramref name="connection" /> or
        ///     <paramref name="configuration" /> are <c>null</c>.
        /// </exception>
        public AsyncSnapshotReader(IEventStoreConnection connection, SnapshotReaderConfiguration configuration)
        {
            Connection = connection ?? throw new ArgumentNullException(nameof(connection));
            Configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        }

        /// <summary>
        ///     Gets the event store connection.
        /// </summary>
        /// <value>
        ///     The connection.
        /// </value>
        public IEventStoreConnection Connection { get; }

        /// <summary>
        ///     Gets the configuration used to read.
        /// </summary>
        /// <value>
        ///     The configuration.
        /// </value>
        public SnapshotReaderConfiguration Configuration { get; }

        /// <summary>
        ///     Reads a snapshot from the underlying storage if one is present.
        /// </summary>
        /// <param name="identifier">The aggregate identifier.</param>
        /// <returns>
        ///     A <see cref="Snapshot" /> if found, otherwise <c>empty</c>.
        /// </returns>
        /// <exception cref="System.ArgumentNullException">identifier</exception>
        public async Task<Optional<Snapshot>> ReadOptionalAsync(string identifier)
        {
            if (identifier == null)
            {
                throw new ArgumentNullException(nameof(identifier));
            }
            UserCredentials streamUserCredentials = Configuration.StreamUserCredentialsResolver.Resolve(identifier);
            string streamName = Configuration.StreamNameResolver.Resolve(identifier);
            StreamEventsSlice slice =
                await
                    Connection.ReadStreamEventsBackwardAsync(streamName, StreamPosition.End, 1, false, streamUserCredentials);
            if (slice.Status == SliceReadStatus.StreamDeleted || slice.Status == SliceReadStatus.StreamNotFound ||
                slice.Events.Length == 0 && slice.NextEventNumber == -1)
            {
                return Optional<Snapshot>.Empty;
            }
            return new Optional<Snapshot>(Configuration.Deserializer.Deserialize(slice.Events[0]));
        }
    }
}
