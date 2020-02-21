using System;

namespace Heleus.Base
{
    public class Config
    {
        string _fileName;
        Storage _storage;

        protected virtual void Loaded()
        {

        }

        public static T Load<T>(Storage storage, bool createEmpty = true) where T : Config, new()
        {
            var _config = default(T);
            var fileName = typeof(T).Name.ToLower() + ".txt";

            var json = storage.ReadFileText(fileName);
            if (json == null && !createEmpty)
                return _config;

            var error = false;
            try
            {
                if (!string.IsNullOrEmpty(json))
                    _config = Json.ToObject<T>(json);
            }
            catch(Exception ex)
            {
                Log.Error($"Parsing {fileName} failed: {ex}");
                error = true;
            }

            if (_config == null)
                _config = new T();

            _config._fileName = fileName;
            _config._storage = storage;
            _config.Loaded();

            if(!error) // don't override on error
                Save(_config);

            return _config;
        }

        public static void Save(Config config)
        {
            config._storage.WriteFileText(config._fileName, Json.ToNiceJson(config));
        }
    }
}
