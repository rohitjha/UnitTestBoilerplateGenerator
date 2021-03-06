﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using UnitTestBoilerplate.Model;
using UnitTestBoilerplate.Utilities;

namespace UnitTestBoilerplate.Services
{
	public class TestGenerationService
	{
		private static readonly HashSet<string> PropertyInjectionAttributeNames = new HashSet<string>
		{
			"Microsoft.Practices.Unity.DependencyAttribute",
			"Ninject.InjectAttribute",
			"Grace.DependencyInjection.Attributes.ImportAttribute"
		};

		private static readonly IList<string> ClassSuffixes = new List<string>
		{
			"ViewModel",
			"Service",
			"Provider",
			"Factory",
			"Manager",
			"Component"
		};

		public async Task<string> GenerateUnitTestFileAsync(
			ProjectItemSummary selectedFile,
			EnvDTE.Project targetProject, 
			TestFramework testFramework,
			MockFramework mockFramework)
		{
			string sourceProjectDirectory = Path.GetDirectoryName(selectedFile.ProjectFilePath);
			string selectedFileDirectory = Path.GetDirectoryName(selectedFile.FilePath);

			if (sourceProjectDirectory == null || selectedFileDirectory == null || !selectedFileDirectory.StartsWith(sourceProjectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Error with selected file paths.");
			}

			string relativePath = this.GetRelativePath(selectedFile);

			TestGenerationContext context = await this.CollectTestGenerationContextAsync(selectedFile, targetProject, testFramework, mockFramework);

			string unitTestContents = this.GenerateUnitTestContents(context);

			string testFolder = Path.Combine(Path.GetDirectoryName(targetProject.FullName), relativePath);
			string testPath = Path.Combine(testFolder, context.ClassName + "Tests.cs");

			if (File.Exists(testPath))
			{
				throw new InvalidOperationException("Test file already exists.");
			}

			if (!Directory.Exists(testFolder))
			{
				Directory.CreateDirectory(testFolder);
			}

			File.WriteAllText(testPath, unitTestContents);

			return testPath;
		}

		public async Task GenerateUnitTestFileAsync(
			ProjectItemSummary selectedFile,
			string targetFilePath,
			string targetProjectNamespace,
			TestFramework testFramework,
			MockFramework mockFramework)
		{
			string sourceProjectDirectory = Path.GetDirectoryName(selectedFile.ProjectFilePath);
			string selectedFileDirectory = Path.GetDirectoryName(selectedFile.FilePath);

			if (sourceProjectDirectory == null || selectedFileDirectory == null || !selectedFileDirectory.StartsWith(sourceProjectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Error with selected file paths.");
			}

			TestGenerationContext context = await this.CollectTestGenerationContextAsync(selectedFile, targetProjectNamespace, testFramework, mockFramework);

			string unitTestContents = this.GenerateUnitTestContents(context);

			string testFolder = Path.GetDirectoryName(targetFilePath);

			if (File.Exists(targetFilePath))
			{
				throw new InvalidOperationException("Test file already exists.");
			}

			if (!Directory.Exists(testFolder))
			{
				Directory.CreateDirectory(testFolder);
			}

			File.WriteAllText(targetFilePath, unitTestContents);
		}

		public string GetRelativePath(ProjectItemSummary selectedFile)
		{
			string projectDirectory = Path.GetDirectoryName(selectedFile.ProjectFilePath);
			string selectedFileDirectory = Path.GetDirectoryName(selectedFile.FilePath);

			if (projectDirectory == null || selectedFileDirectory == null || !selectedFileDirectory.StartsWith(projectDirectory, StringComparison.OrdinalIgnoreCase))
			{
				throw new InvalidOperationException("Error with selected file paths.");
			}

			string relativePath = selectedFileDirectory.Substring(projectDirectory.Length);
			if (relativePath.StartsWith("\\", StringComparison.Ordinal))
			{
				relativePath = relativePath.Substring(1);
			}

			return relativePath;
		}

		private async Task<TestGenerationContext> CollectTestGenerationContextAsync(
			ProjectItemSummary selectedFile, 
			string targetProjectNamespace, 
			TestFramework testFramework,
			MockFramework mockFramework)
		{
			Microsoft.CodeAnalysis.Solution solution = CreateUnitTestBoilerplateCommandPackage.VisualStudioWorkspace.CurrentSolution;
			DocumentId documentId = solution.GetDocumentIdsWithFilePath(selectedFile.FilePath).FirstOrDefault();
			if (documentId == null)
			{
				throw new InvalidOperationException("Could not find document in solution with file path " + selectedFile.FilePath);
			}

			var document = solution.GetDocument(documentId);

			SyntaxNode root = await document.GetSyntaxRootAsync();
			SemanticModel semanticModel = await document.GetSemanticModelAsync();

			SyntaxNode firstClassDeclaration = root.DescendantNodes().FirstOrDefault(node => node.Kind() == SyntaxKind.ClassDeclaration);

			if (firstClassDeclaration == null)
			{
				throw new InvalidOperationException("Could not find class declaration.");
			}

			if (firstClassDeclaration.ChildTokens().Any(node => node.Kind() == SyntaxKind.AbstractKeyword))
			{
				throw new InvalidOperationException("Cannot unit test an abstract class.");
			}

			SyntaxToken classIdentifierToken = firstClassDeclaration.ChildTokens().FirstOrDefault(n => n.Kind() == SyntaxKind.IdentifierToken);
			if (classIdentifierToken == default(SyntaxToken))
			{
				throw new InvalidOperationException("Could not find class identifier.");
			}

			NamespaceDeclarationSyntax namespaceDeclarationSyntax = null;
			if (!TypeUtilities.TryGetParentSyntax(firstClassDeclaration, out namespaceDeclarationSyntax))
			{
				throw new InvalidOperationException("Could not find class namespace.");
			}

			// Find property injection types
			var injectableProperties = new List<InjectableProperty>();

			string classFullName = namespaceDeclarationSyntax.Name + "." + classIdentifierToken;
			INamedTypeSymbol classType = semanticModel.Compilation.GetTypeByMetadataName(classFullName);

			foreach (ISymbol member in classType.GetBaseTypesAndThis().SelectMany(n => n.GetMembers()))
			{
				if (member.Kind == SymbolKind.Property)
				{
					IPropertySymbol property = (IPropertySymbol)member;

					foreach (AttributeData attribute in property.GetAttributes())
					{
						if (PropertyInjectionAttributeNames.Contains(attribute.AttributeClass.ToString()))
						{
							var injectableProperty = InjectableProperty.TryCreateInjectableProperty(property.Name, property.Type.ToString(), mockFramework);
							if (injectableProperty != null)
							{
								injectableProperties.Add(injectableProperty);
							}
						}
					}
				}
			}

			string className = classIdentifierToken.ToString();

			// Find constructor injection types
			var constructorInjectionTypes = new List<InjectableType>();
			SyntaxNode constructorDeclaration = firstClassDeclaration.ChildNodes().FirstOrDefault(n => n.Kind() == SyntaxKind.ConstructorDeclaration);
			if (constructorDeclaration != null)
			{
				SyntaxNode parameterListNode = constructorDeclaration.ChildNodes().First(n => n.Kind() == SyntaxKind.ParameterList);
				var parameterNodes = parameterListNode.ChildNodes().Where(n => n.Kind() == SyntaxKind.Parameter);

				foreach (SyntaxNode node in parameterNodes)
				{
					constructorInjectionTypes.Add(InjectableType.TryCreateInjectableTypeFromParameterNode(node, semanticModel, mockFramework));
				}
			}

			string unitTestNamespace;

			string relativePath = this.GetRelativePath(selectedFile);

			if (string.IsNullOrEmpty(relativePath))
			{
				unitTestNamespace = targetProjectNamespace;
			}
			else
			{
				List<string> defaultNamespaceParts = targetProjectNamespace.Split('.').ToList();
				List<string> unitTestNamespaceParts = new List<string>(defaultNamespaceParts);
				unitTestNamespaceParts.AddRange(relativePath.Split('\\'));

				unitTestNamespace = string.Join(".", unitTestNamespaceParts);
			}

			List<InjectableType> injectedTypes = new List<InjectableType>(injectableProperties);
			injectedTypes.AddRange(constructorInjectionTypes.Where(t => t != null));

			GenerateMockNames(injectedTypes);

			return new TestGenerationContext(
				mockFramework,
				testFramework,
				unitTestNamespace,
				className,
				namespaceDeclarationSyntax.Name.ToString(),
				injectableProperties,
				constructorInjectionTypes,
				injectedTypes);
		}

		private async Task<TestGenerationContext> CollectTestGenerationContextAsync(ProjectItemSummary selectedFile, EnvDTE.Project targetProject, TestFramework testFramework, MockFramework mockFramework)
		{
			string targetProjectNamespace = targetProject.Properties.Item("DefaultNamespace").Value as string;
			return await this.CollectTestGenerationContextAsync(selectedFile, targetProjectNamespace, testFramework, mockFramework);
		}

		private string GenerateUnitTestContents(TestGenerationContext context)
		{
			TestFramework testFramework = context.TestFramework;
			MockFramework mockFramework = context.MockFramework;

			string fileTemplate = StaticBoilerplateSettings.GetTemplate(testFramework, mockFramework, TemplateType.File);
			string filledTemplate = StringUtilities.ReplaceTokens(
				fileTemplate,
				(tokenName, propertyIndex, builder) =>
				{
					switch (tokenName)
					{
						case "UsingStatements":
							WriteUsings(builder, context);
							break;
						case "Namespace":
							builder.Append(context.UnitTestNamespace);
							break;
						case "MockFieldDeclarations":
							WriteMockFieldDeclarations(builder, context);
							break;
						case "MockFieldInitializations":
							WriteMockFieldInitializations(builder, context);
							break;
						case "ExplicitConstructor":
							WriteExplicitConstructor(builder, context, FindIndent(fileTemplate, propertyIndex));
							break;
						case "ClassName":
							builder.Append(context.ClassName);
							break;
						case "ClassNameShort":
							builder.Append(GetShortClassName(context.ClassName));
							break;
						case "ClassNameShortLower":
							// Legacy, new syntax is ClassNameShort.CamelCase
							builder.Append(GetShortClassNameLower(context.ClassName));
							break;
						default:
							// We didn't recognize it, just pass through.
							builder.Append($"${tokenName}$");
							break;
					}
				});

			SyntaxTree tree = CSharpSyntaxTree.ParseText(filledTemplate);
			SyntaxNode formattedNode = Formatter.Format(tree.GetRoot(), CreateUnitTestBoilerplateCommandPackage.VisualStudioWorkspace);

			return formattedNode.ToString();
		}

		private static void WriteUsings(StringBuilder builder, TestGenerationContext context)
		{
			List<string> namespaces = new List<string>();
			namespaces.AddRange(context.MockFramework.UsingNamespaces);
			namespaces.Add(context.TestFramework.UsingNamespace);
			namespaces.Add(context.ClassNamespace);

			if (context.TestFramework.TestCleanupStyle == TestCleanupStyle.Disposable)
			{
				namespaces.Add("System");
			}

			foreach (InjectableType injectedType in context.InjectedTypes)
			{
				namespaces.AddRange(injectedType.TypeNamespaces);
			}

			namespaces = namespaces.Distinct().ToList();
			namespaces.Sort(StringComparer.Ordinal);

			for (int i = 0; i < namespaces.Count; i++)
			{
				builder.Append($"using {namespaces[i]};");

				if (i < namespaces.Count - 1)
				{
					builder.AppendLine();
				}
			}
		}

		private static void WriteMockFieldDeclarations(StringBuilder builder, TestGenerationContext context)
		{
			string template = StaticBoilerplateSettings.GetTemplate(context.TestFramework, context.MockFramework, TemplateType.MockFieldDeclaration);
			WriteFieldLines(builder, context, template);
		}

		private static void WriteMockFieldInitializations(StringBuilder builder, TestGenerationContext context)
		{
			string template = StaticBoilerplateSettings.GetTemplate(context.TestFramework, context.MockFramework, TemplateType.MockFieldInitialization);
			WriteFieldLines(builder, context, template);
		}

		// Works for both field declarations and initializations.
		private static void WriteFieldLines(StringBuilder builder, TestGenerationContext context, string template)
		{
			for (int i = 0; i < context.InjectedTypes.Count; i++)
			{
				InjectableType injectedType = context.InjectedTypes[i];
				string line = ReplaceInterfaceTokens(template, injectedType);

				builder.Append(line);

				if (i < context.InjectedTypes.Count - 1)
				{
					builder.AppendLine();
				}
			}
		}

		private static void WriteExplicitConstructor(StringBuilder builder, TestGenerationContext context, string currentIndent)
		{
			builder.Append($"new {context.ClassName}");

			if (context.ConstructorTypes.Count > 0)
			{
				builder.AppendLine("(");

				for (int i = 0; i < context.ConstructorTypes.Count; i++)
				{
					string mockReferenceStatement;
					InjectableType constructorType = context.ConstructorTypes[i];
					if (constructorType == null)
					{
						mockReferenceStatement = "TODO";
					}
					else
					{
						string template = StaticBoilerplateSettings.GetTemplate(context.TestFramework, context.MockFramework, TemplateType.MockObjectReference);
						mockReferenceStatement = ReplaceInterfaceTokens(template, constructorType);
					}

					builder.Append($"{currentIndent}    {mockReferenceStatement}");

					if (i < context.ConstructorTypes.Count - 1)
					{
						builder.AppendLine(",");
					}
				}

				builder.Append(")");
			}
			else if (context.Properties.Count == 0)
			{
				builder.Append("()");
			}

			if (context.Properties.Count > 0)
			{
				builder.AppendLine();
				builder.AppendLine("{");

				foreach (InjectableProperty property in context.Properties)
				{
					string template = StaticBoilerplateSettings.GetTemplate(context.TestFramework, context.MockFramework, TemplateType.MockObjectReference);
					string mockReferenceStatement = ReplaceInterfaceTokens(template, property);

					builder.AppendLine($"{property.PropertyName} = {mockReferenceStatement},");
				}

				builder.Append(@"}");
			}
		}

		private static string ReplaceInterfaceTokens(string template, InjectableType injectableType)
		{
			return StringUtilities.ReplaceTokens(
				template,
				(tokenName, propertyIndex, builder) =>
				{
					switch (tokenName)
					{
						case "InterfaceName":
							builder.Append(injectableType.TypeName);
							break;

						case "InterfaceNameBase":
							builder.Append(injectableType.TypeBaseName);
							break;

						case "InterfaceType":
							builder.Append(injectableType.ToString());
							break;

						case "InterfaceMockName":
							builder.Append(injectableType.MockName);
							break;

						default:
							// We didn't recognize it, just pass through.
							builder.Append($"${tokenName}$");
							break;
					}
				});
		}



		private static string FindIndent(string template, int currentIndex)
		{
			// Go back and find line start
			int lineStart = -1;
			for (int i = currentIndex - 1; i >= 0; i--)
			{
				char c = template[i];
				if (c == '\n')
				{
					lineStart = i + 1;
					break;
				}
			}

			// Go forward and find first non-whitespace character
			for (int i = lineStart; i <= currentIndex; i++)
			{
				char c = template[i];
				if (c != ' ' && c != '\t')
				{
					return template.Substring(lineStart, i - lineStart);
				}
			}

			return string.Empty;
		}

		private static string GetShortClassName(string className)
		{
			string pascalCaseShortClassName = null;
			foreach (string suffix in ClassSuffixes)
			{
				if (className.EndsWith(suffix))
				{
					pascalCaseShortClassName = suffix;
					break;
				}
			}

			if (pascalCaseShortClassName == null)
			{
				pascalCaseShortClassName = className;
			}

			return pascalCaseShortClassName;
		}

		private static string GetShortClassNameLower(string className)
		{
			string shortName = GetShortClassName(className);
			return shortName.Substring(0, 1).ToLowerInvariant() + shortName.Substring(1);
		}

		private static void GenerateMockNames(List<InjectableType> injectedTypes)
		{
			// Group them by TypeBaseName to see which ones need a more unique name
			var results = from t in injectedTypes
				group t by t.TypeBaseName into g
				select new { TypeBaseName = g.Key, Types = g.ToList() };

			foreach (var result in results)
			{
				if (result.Types.Count == 1)
				{
					result.Types[0].MockName = result.TypeBaseName;
				}
				else
				{
					foreach (var injectedType in result.Types)
					{
						injectedType.MockName = injectedType.LongMockName;
					}
				}
			}
		}
	}
}
