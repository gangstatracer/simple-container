using System;
using System.Reflection;
using SimpleContainer.Configuration;

namespace SimpleContainer
{
	public interface IStaticContainer : IContainer
	{
		IContainer CreateLocalContainer(string name, Assembly primaryAssembly, Action<ContainerConfigurationBuilder> configure);

		//hack, kill
		new IStaticContainer Clone(Action<ContainerConfigurationBuilder> configure);
	}
}