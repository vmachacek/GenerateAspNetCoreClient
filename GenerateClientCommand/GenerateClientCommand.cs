using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using GenerateClientCommand.Extensions;
using GenerateClientCommand.Model;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.ApiExplorer;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.Extensions.Hosting;
using Namotion.Reflection;
using Options;

namespace GenerateClientCommand
{
    public class GenerateClientCommand
    {
        public static void Invoke(Assembly assembly, GenerateClientOptions options)
        {
            var apiExplorer = GetApiExplorer(assembly);
            var clientCollection = GetClientCollection(apiExplorer, options,
                additionalNamespaces: new[] { "System.Threading.Tasks", "Refit" });

            foreach (var clientModel in clientCollection)
            {
                var clientText = CreateClient(clientModel, clientCollection.AmbiguousTypes);

                var path = Path.Combine(options.OutPath, clientModel.Location);
                Directory.CreateDirectory(path);

                File.WriteAllText(Path.Combine(path, $"{clientModel.Name}.cs"), clientText);
            }
        }

        private static string GetCommonNamespacesPart(IEnumerable<IGrouping<ControllerInfo, ApiDescription>> controllerApiDescriptions)
        {
            var namespaces = controllerApiDescriptions
                .Select(c => c.Key)
                .Select(c => c.ControllerTypeInfo.Namespace ?? "");

            return namespaces.GetCommonPart(".");
        }

        private static string[] GetSubPath(ControllerInfo controllerActionDescriptor, string commonNamespace)
        {
            return (controllerActionDescriptor.ControllerTypeInfo.Namespace ?? "")
                .Substring(commonNamespace.Length)
                .Split(".", StringSplitOptions.RemoveEmptyEntries);
        }

        private static ApiDescriptionGroupCollection GetApiExplorer(Assembly assembly)
        {
            var entryType = assembly.EntryPoint?.DeclaringType;
            var hostBuilderMethod = entryType?.GetMethod("CreateHostBuilder");

            IServiceProvider? services = null;

            if (hostBuilderMethod != null)
            {
                var hostBuilder = hostBuilderMethod.Invoke(null, new[] { Array.Empty<string>() }) as IHostBuilder;
                var host = hostBuilder?.Build();
                services = host?.Services;
            }

            var webHostBuilderMethod = entryType?.GetMethod("CreateWebHostBuilder");
            if (webHostBuilderMethod != null)
            {
                var webHostBuilder = webHostBuilderMethod.Invoke(null, new[] { Array.Empty<string>() }) as IWebHostBuilder;
                var webHost = webHostBuilder?.Build();
                services = webHost?.Services;
            }

            if (services == null)
            {
                throw new Exception("Entry class should have either 'CreateHostBuilder', or 'CreateWebHostBuilder' method");
            }

            var apiExplorerProvider = (IApiDescriptionGroupCollectionProvider)services.GetService(typeof(IApiDescriptionGroupCollectionProvider));

            return apiExplorerProvider.ApiDescriptionGroups;
        }

        private static string CreateClient(Client clientModel, HashSet<Type> ambiguousTypes)
        {
            var methodDescriptions = clientModel.EndpointMethods.Select(endpointMethod =>
            {
                var xmlDoc = endpointMethod.XmlDoc;

                if (!string.IsNullOrEmpty(xmlDoc))
                    xmlDoc += Environment.NewLine;

                var multipartAttribute = endpointMethod.IsMultipart
                    ? "[Multipart]" + Environment.NewLine
                    : "";

                var parameterStrings = endpointMethod.Parameters
                    .Select(p =>
                    {
                        var attribute = p.Source == ParameterSource.Body ? "[Body] " : "";
                        var type = p.Source == ParameterSource.File ? "MultipartItem" : p.Type.GetName(ambiguousTypes);
                        var defaultValue = p.DefaultValueLiteral == null ? "" : " = " + p.DefaultValueLiteral;
                        return $"{attribute}{type} {p.Name}{defaultValue}";
                    })
                    .ToArray();

                var httpMethodAttribute = endpointMethod.HttpMethod.ToString().ToPascalCase();
                var methodPathAttribute = $@"[{httpMethodAttribute}(""/{endpointMethod.Path}"")]";

                return
$@"{xmlDoc}{multipartAttribute}{methodPathAttribute}
{endpointMethod.ResponseType.WrapInTask().GetName(ambiguousTypes)} {endpointMethod.Name}({string.Join(", ", parameterStrings)});";
            }).ToArray();

            return
$@"//<auto-generated />

{string.Join(Environment.NewLine, clientModel.ImportedNamespaces.Select(n => $"using {n};"))}

namespace {clientModel.Namespace}
{{
    public interface {clientModel.Name}
    {{
{string.Join(Environment.NewLine + Environment.NewLine, methodDescriptions).Indent("        ")}
    }}
}}";
        }

        private static List<ApiDescription> HandleDuplicates(List<ApiDescription> apiDescriptions)
        {
            var conflictingApisGroups = apiDescriptions
                .GroupBy(api => ((ControllerActionDescriptor)api.ActionDescriptor).ActionName
                    + string.Concat(api.ParameterDescriptions.Select(p => p.Type?.FullName ?? "-")))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var conflictingApis in conflictingApisGroups)
            {
                // Take suffixes from path
                var commonPathPart = conflictingApis.Select(api => api.RelativePath).GetCommonPart("/");

                foreach (var api in conflictingApis)
                {
                    var suffix = api.RelativePath == commonPathPart
                        ? ""
                        : api.RelativePath.Substring(commonPathPart.Length + 1).ToPascalCase();

                    ((ControllerActionDescriptor)api.ActionDescriptor).ActionName += suffix;
                }
            }

            return apiDescriptions;
        }

        private static HashSet<Type> GetAmbiguousTypes(IEnumerable<string> namespaces)
        {
            var namespacesSet = namespaces.ToHashSet();

            return AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic)
                .SelectMany(a =>
                {
                    try
                    {
                        return a.ExportedTypes;
                    }
                    catch
                    {
                        return Array.Empty<Type>();
                    }
                })
                .Where(t => t.DeclaringType == null && namespacesSet.Contains(t.Namespace!))
                .GroupBy(t => t.Name)
                .Where(g => g.Select(t => t.Namespace).Distinct().Count() > 1)
                .SelectMany(g => g)
                .ToHashSet();
        }

        private static string? GetXmlDoc(ApiDescription apiDescription)
        {
            var xmlElement = (apiDescription.ActionDescriptor as ControllerActionDescriptor)?.MethodInfo.GetXmlDocsElement();

            if (xmlElement == null)
                return null;

            var xmlLines = xmlElement.Elements()
                .Select(e => e.ToString())
                .SelectMany(s => s.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.RemoveEmptyEntries))
                .Select(line => line.Trim().Replace("cref=\"T:", "cref=\""));

            var xmlDoc = string.Join(Environment.NewLine, xmlLines).Indent("/// ");

            return xmlDoc;
        }

        private static Type? GetResponseType(ApiDescription apiDescription)
        {
            return apiDescription.SupportedResponseTypes
                .OrderBy(r => r.StatusCode)
                .FirstOrDefault(r => r.StatusCode >= 200 && r.StatusCode < 300)
                ?.Type;
        }

        private static ClientCollection GetClientCollection(
            ApiDescriptionGroupCollection apiExplorer,
            GenerateClientOptions options,
            string[] additionalNamespaces)
        {
            var apiDescriptions = apiExplorer.Items
                .SelectMany(i => i.Items)
                .Where(i => i.ActionDescriptor is ControllerActionDescriptor)
                .ToList();

            if (!string.IsNullOrEmpty(options.ExcludeTypes))
            {
                apiDescriptions.RemoveAll(api
                    => ((ControllerActionDescriptor)api.ActionDescriptor).ControllerTypeInfo.FullName?.Contains(options.ExcludeTypes) == true);
            }

            if (!string.IsNullOrEmpty(options.ExcludePaths))
            {
                apiDescriptions.RemoveAll(api => ("/" + api.RelativePath).Contains(options.ExcludePaths));
            }

            var allNamespaces = GetNamespaces(apiDescriptions);
            var ambiguousTypes = GetAmbiguousTypes(allNamespaces);

            var controllerApiDescriptions = apiDescriptions.GroupBy(i => ControllerInfo.From((ControllerActionDescriptor)i.ActionDescriptor));

            string commonControllerNamespacePart = GetCommonNamespacesPart(controllerApiDescriptions);

            var clients = controllerApiDescriptions.Select(apis =>
                GetClientModel(
                    commonControllerNamespace: commonControllerNamespacePart,
                    outputNamespace: options.Namespace,
                    additionalNamespaces: additionalNamespaces,
                    controllerInfo: apis.Key,
                    apiDescriptions: apis.ToList(),
                    ambiguousTypes: ambiguousTypes)
                ).ToList();

            return new ClientCollection(clients, ambiguousTypes);
        }

        private static Client GetClientModel(
            string commonControllerNamespace,
            string outputNamespace,
            string[] additionalNamespaces,
            ControllerInfo controllerInfo,
            List<ApiDescription> apiDescriptions,
            HashSet<Type> ambiguousTypes)
        {
            apiDescriptions = HandleDuplicates(apiDescriptions);

            var subPath = GetSubPath(controllerInfo, commonControllerNamespace);

            var name = $"I{controllerInfo.ControllerName}Api";
            var clientNamespace = string.Join(".", new[] { outputNamespace }.Concat(subPath));

            var namespaces = GetNamespaces(apiDescriptions, ambiguousTypes)
                .Concat(additionalNamespaces)
                .OrderByDescending(ns => ns.StartsWith("System"))
                .ThenBy(ns => ns)
                .ToList();

            var methods = apiDescriptions.Select(GetEndpointMethod).ToList();

            return new Client
            (
                location: Path.Combine(subPath),
                importedNamespaces: namespaces,
                @namespace: clientNamespace,
                name: name,
                endpointMethods: methods
            );
        }

        private static EndpointMethod GetEndpointMethod(ApiDescription apiDescription)
        {
            var responseType = GetResponseType(apiDescription);

            if (responseType == null)
            {
                Console.WriteLine($"Cannot find return type for " + apiDescription.ActionDescriptor.DisplayName);
                responseType = typeof(void);
            }

            return new EndpointMethod
            (
                xmlDoc: GetXmlDoc(apiDescription),
                httpMethod: new HttpMethod(apiDescription.HttpMethod),
                path: apiDescription.RelativePath,
                responseType: responseType,
                name: ((ControllerActionDescriptor)apiDescription.ActionDescriptor).ActionName,
                parameters: GetParameters(apiDescription)
            );
        }

        private static List<Parameter> GetParameters(ApiDescription apiDescription)
        {
            var parametersList = new List<Parameter>();

            for (int i = 0; i < apiDescription.ParameterDescriptions.Count; i++)
            {
                var parameterDescription = apiDescription.ParameterDescriptions[i];

                if (parameterDescription.ParameterDescriptor?.ParameterType == typeof(CancellationToken))
                    continue;

                if (parameterDescription.ParameterDescriptor?.ParameterType == typeof(IFormFile))
                {
                    var name = parameterDescription.ParameterDescriptor.Name;

                    parametersList.Add(new Parameter(
                        source: ParameterSource.File,
                        type: typeof(IFormFile),
                        name: name.ToCamelCase(),
                        defaultValueLiteral: null));

                    // Skip parameters that correspond to same file
                    while (i + 1 < apiDescription.ParameterDescriptions.Count
                        && apiDescription.ParameterDescriptions[i + 1].ParameterDescriptor?.ParameterType == typeof(IFormFile)
                        && apiDescription.ParameterDescriptions[i + 1].ParameterDescriptor?.Name == name)
                    {
                        i++;
                    }

                    continue;
                }

                if (parametersList.Any(p => p.Name.Equals(parameterDescription.Name, StringComparison.OrdinalIgnoreCase)))
                {
                    Console.WriteLine($"Duplicate parameter '{parameterDescription.Name}' for '{apiDescription.ActionDescriptor.DisplayName}'");
                    continue;
                }

                var source = parameterDescription.Source.Id switch
                {
                    "Body" => ParameterSource.Body,
                    "Path" => ParameterSource.Path,
                    "FormFile" => ParameterSource.File,
                    "Query" => ParameterSource.Query,
                    _ => ParameterSource.Query
                };

                var defaultValue = GetDefaultValueLiteral(parameterDescription);

                var type = parameterDescription.Type ?? typeof(string);

                if (defaultValue != null)
                {
                    type = type.ToNullable();
                }

                parametersList.Add(new Parameter(
                    source: source,
                    type: type,
                    name: parameterDescription.Name.ToCamelCase(),
                    defaultValueLiteral: defaultValue));
            }

            return parametersList;
        }

        private static List<string> GetNamespaces(IEnumerable<ApiDescription> apiDescriptions, HashSet<Type>? ambiguousTypes = null)
        {
            var namespaces = new HashSet<string>();

            foreach (var apiDescription in apiDescriptions)
            {
                var responseType = GetResponseType(apiDescription);
                AddForType(responseType);

                foreach (var parameterDescription in apiDescription.ParameterDescriptions)
                {
                    // Skip FormFile, as it won't be present in result file 
                    // (not needed for 3.1+)
                    if (parameterDescription.Source.Id != "FormFile")
                    {
                        AddForType(parameterDescription.Type);
                    }
                }
            }

            return namespaces.ToList();

            void AddForType(Type? type)
            {
                if (type != null && !type.IsBuiltInType() && ambiguousTypes?.Contains(type) != true)
                {
                    if (type.Namespace != null)
                        namespaces.Add(type.Namespace);

                    if (type.IsGenericType)
                    {
                        foreach (var typeArg in type.GetGenericArguments())
                            AddForType(typeArg);
                    }
                }
            }
        }

        private static string? GetDefaultValueLiteral(ApiParameterDescription parameter)
        {
            // Use reflection for AspNetCore 2.1 compatibility.
            var defaultValue = parameter.TryGetPropertyValue<object>(nameof(parameter.DefaultValue));

            if (defaultValue != null)
            {
                // If defaultValue is not null - return it.
                return defaultValue.ToLiteral();
            }
            else if (parameter.TryGetPropertyValue(nameof(parameter.IsRequired), true) == false)
            {
                // If defaultValue is null, but value is not required - return it anyway.
                return defaultValue.ToLiteral();
            }

            // If query parameter - use default null.
            if (parameter.Source == BindingSource.Query)
            {
                return "null";
            }

            return null;
        }
    }
}