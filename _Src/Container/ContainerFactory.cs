using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using SimpleContainer.Configuration;
using SimpleContainer.Helpers;
using SimpleContainer.Implementation;
using SimpleContainer.Interface;

namespace SimpleContainer
{
	public class ContainerFactory
	{
		private Type profile;
		private static readonly Func<AssemblyName, bool> defaultAssembliesFilter = n => n.Name != "NUnit3.TestAdapter";
		private Func<AssemblyName, bool> assembliesFilter = defaultAssembliesFilter;
		private Func<Type, string, object> settingsLoader;
		private Func<string> configTextProvider;
		private LogError errorLogger;
		private LogInfo infoLogger;
		private Type[] priorities;
		private Action<ContainerConfigurationBuilder> configure;
		private Action<ContainerConfigurationBuilder> staticConfigure;
		private TypesContext typesContextCache;
		private Func<Type[]> types;
		private IParametersSource parameters;
		private readonly Dictionary<Type, Func<object, string>> valueFormatters = new Dictionary<Type, Func<object, string>>();

		private readonly Dictionary<Type, ConfigurationRegistry> configurationByProfileCache =
			new Dictionary<Type, ConfigurationRegistry>();

		public ContainerFactory WithSettingsLoader(Func<Type, object> newLoader)
		{
			var cache = new ConcurrentDictionary<Type, object>();
			settingsLoader = (t, _) => cache.GetOrAdd(t, newLoader);
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithSettingsLoader(Func<Type, string, object> newLoader)
		{
			var cache = new ConcurrentDictionary<string, object>();
			settingsLoader = (t, k) => cache.GetOrAdd(t.Name + k, s => newLoader(t, k));
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithParameters(IParametersSource newParameters)
		{
			parameters = newParameters;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithValueFormatter<T>(Func<T, string> formatter)
		{
			valueFormatters[typeof (T)] = o => formatter((T) o);
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithAssembliesFilter(Func<AssemblyName, bool> newAssembliesFilter)
		{
			assembliesFilter = n => defaultAssembliesFilter(n) && (newAssembliesFilter(n) || n.Name == "SimpleContainer");
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithPriorities(params Type[] newConfiguratorTypes)
		{
			priorities = newConfiguratorTypes;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithConfigFile(string fileName)
		{
			configTextProvider = () => fileName != null && File.Exists(fileName)
				? File.ReadAllText(fileName)
				: null;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithConfigText(Func<string> getConfigText)
		{
			configTextProvider = getConfigText;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithConfigText(string configText)
		{
			configTextProvider = () => configText;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithErrorLogger(LogError logger)
		{
			errorLogger = logger;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithInfoLogger(LogInfo logger)
		{
			infoLogger = logger;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithProfile(Type newProfile)
		{
			if (newProfile != null && !typeof (IProfile).IsAssignableFrom(newProfile))
				throw new SimpleContainerException(string.Format("profile type [{0}] must inherit from IProfile",
					newProfile.FormatName()));
			profile = newProfile;
			return this;
		}

		public ContainerFactory WithConfigurator(Action<ContainerConfigurationBuilder> newConfigure)
		{
			configure = newConfigure;
			return this;
		}

		public ContainerFactory WithStaticConfigurator(Action<ContainerConfigurationBuilder> newConfigure)
		{
			staticConfigure = newConfigure;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public ContainerFactory WithTypesFromDefaultBinDirectory(bool withExecutables)
		{
			return WithTypesFromDirectory(GetBinDirectory(), withExecutables);
		}

		public ContainerFactory WithTypesFromDirectory(string directory, bool withExecutables)
		{
			var assemblies = Directory.GetFiles(directory, "*.dll")
				.Union(withExecutables ? Directory.GetFiles(directory, "*.exe") : Enumerable.Empty<string>())
				.Select(delegate(string s)
				{
					try
					{
						return AssemblyName.GetAssemblyName(s);
					}
					catch (BadImageFormatException)
					{
						return null;
					}
				})
				.NotNull()
				.Where(assembliesFilter)
				.AsParallel()
				.Select(AssemblyHelpers.LoadAssembly);
			return WithTypesFromAssemblies(assemblies);
		}

		public ContainerFactory WithTypesFromAssemblies(IEnumerable<Assembly> assemblies)
		{
			return WithTypesFromAssemblies(assemblies.AsParallel());
		}

		public ContainerFactory WithTypes(Type[] newTypes)
		{
			types = () => newTypes;
			typesContextCache = null;
			configurationByProfileCache.Clear();
			return this;
		}

		public IContainer Build()
		{
			var typesContext = typesContextCache;
			if (typesContext == null)
			{
				var targetTypes = types()
					.Concat(Assembly.GetExecutingAssembly().GetTypes())
					.Where(x => !x.Name.StartsWith("<>", StringComparison.OrdinalIgnoreCase))
					.Distinct()
					.ToArray();
				typesContext = new TypesContext {typesList = TypesList.Create(targetTypes)};
				typesContext.genericsAutoCloser = new GenericsAutoCloser(typesContext.typesList, assembliesFilter);
				var configText = configTextProvider?.Invoke();
				if (!string.IsNullOrEmpty(configText))
					typesContext.textConfigurator = ConfigurationTextParser.Parse(typesContext.typesList.Types, configText);
				ConfigurationRegistry staticConfiguration;
				if (staticConfigure == null)
					staticConfiguration = ConfigurationRegistry.Empty;
				else
				{
					var builder = new ContainerConfigurationBuilder();
					staticConfigure(builder);
					staticConfiguration = builder.RegistryBuilder.Build(typesContext.typesList, null);
				}
				var configurationContainer = CreateContainer(typesContext, staticConfiguration);
				typesContext.configuratorRunner = configurationContainer.Get<ConfiguratorRunner>();
				typesContextCache = typesContext;
			}

			if (!configurationByProfileCache.TryGetValue(profile ?? typeof (ContainerFactory), out var configurationRegistry))
			{
				var builder = new ContainerConfigurationBuilder();
				var configurationContext = new ConfigurationContext(profile, settingsLoader, parameters);
				typesContext.configuratorRunner.Run(builder, configurationContext, priorities);
				typesContext.textConfigurator?.Invoke(builder);
				configurationRegistry = builder.RegistryBuilder.Build(typesContext.typesList, null);
				configurationByProfileCache.Add(profile ?? typeof (ContainerFactory), configurationRegistry);
			}
			return CreateContainer(typesContext, configurationRegistry.Apply(typesContext.typesList, configure));
		}

		private IContainer CreateContainer(TypesContext currentTypesContext, ConfigurationRegistry configuration)
		{
			return new Implementation.SimpleContainer(configuration, new ContainerContext
			{
				infoLogger = infoLogger,
				genericsAutoCloser = currentTypesContext.genericsAutoCloser,
				typesList = currentTypesContext.typesList,
				valueFormatters = valueFormatters
			}, errorLogger);
		}

		private static string GetBinDirectory()
		{
			var relativePath = AppDomain.CurrentDomain.RelativeSearchPath;
			var basePath = AppDomain.CurrentDomain.BaseDirectory;
			return string.IsNullOrEmpty(relativePath) || !relativePath.IsSubdirectoryOf(basePath)
				? basePath
				: relativePath;
		}

		private ContainerFactory WithTypesFromAssemblies(ParallelQuery<Assembly> assemblies)
		{
			var newTypes = assemblies
				.Where(x => assembliesFilter(x.GetName()))
				.SelectMany(a =>
				{
					try
					{
						return a.GetTypes();
					}
					catch (ReflectionTypeLoadException e)
					{
						var loaderExceptionsText = e.LoaderExceptions.Select(ex => ex.ToString()).JoinStrings(Environment.NewLine);
						var message = $"can't load types from assembly [{a.GetName()}], loaderExceptions:{Environment.NewLine}{loaderExceptionsText}";
						throw new SimpleContainerException(message, e);
					}
				});
			types = newTypes.ToArray;
			typesContextCache = null;
			return this;
		}

		private class TypesContext
		{
			public TypesList typesList;
			public GenericsAutoCloser genericsAutoCloser;
			public Action<ContainerConfigurationBuilder> textConfigurator;
			public ConfiguratorRunner configuratorRunner;
		}
	}
}