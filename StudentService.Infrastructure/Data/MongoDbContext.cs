using MongoDB.Driver;

namespace StudentService.Infrastructure.Data
{
    public class MongoDbContext
    {
        private readonly IMongoDatabase _database;
        private readonly IMongoClient _client;

        public MongoDbContext(string connectionString, string databaseName)
        {
            var settings = MongoClientSettings.FromConnectionString(connectionString);

            // Connection pooling settings
            settings.MaxConnectionPoolSize = 100;
            settings.MinConnectionPoolSize = 10;
            
            // Timeout settings
            settings.ServerSelectionTimeout = TimeSpan.FromSeconds(30);
            settings.ConnectTimeout = TimeSpan.FromSeconds(10);
            settings.SocketTimeout = TimeSpan.FromSeconds(30);
            
            // Retry settings for resilience
            settings.RetryWrites = true;
            settings.RetryReads = true;
            
            // Check if this is a sharded cluster (mongos) or replica set connection
            var isShardedOrReplicaSet = connectionString.Contains("replicaSet=", StringComparison.OrdinalIgnoreCase) 
                || connectionString.Contains(",");
            
            // Check if it's a single standalone instance
            var isSingleInstance = !isShardedOrReplicaSet;
            
            if (isSingleInstance)
            {
                // Single instance settings (for development/simple Docker setup)
                settings.ReadPreference = ReadPreference.Primary;
                settings.WriteConcern = WriteConcern.Acknowledged;
                settings.ReadConcern = ReadConcern.Local;
                settings.DirectConnection = true;
            }
            else
            {
                // Sharded cluster or replica set settings
                // For mongos (sharded cluster), these settings work well
                // For replica set, SecondaryPreferred allows read scaling
                settings.ReadPreference = ReadPreference.SecondaryPreferred;
                settings.WriteConcern = WriteConcern.WMajority;
                settings.ReadConcern = ReadConcern.Majority;
                settings.DirectConnection = false;
            }

            _client = new MongoClient(settings);
            _database = _client.GetDatabase(databaseName);
        }

        public IMongoCollection<T> GetCollection<T>(string name) => _database.GetCollection<T>(name);

        public IMongoClient Client => _client;

        public IMongoDatabase Database => _database;

        public async Task<bool> IsHealthyAsync()
        {
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await _database.RunCommandAsync((Command<MongoDB.Bson.BsonDocument>)"{ping:1}", cancellationToken: cts.Token);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
