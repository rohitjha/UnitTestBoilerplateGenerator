﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using UnitTestBoilerplate.Model;
using UnitTestBoilerplate.Utilities;
using UnitTestBoilerplate.ViewModel;

namespace UnitTestBoilerplate.Services
{
	public class SelfTestService
	{
		// Key is project name, value is the expected detection result
		private static readonly Dictionary<string, SelfTestDetectionResult> ExpectedDetectionResults = new Dictionary<string, SelfTestDetectionResult>
		{
			{ "AutoMoqTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.AutoMoqName) },
			{ "NetCoreAutoMoqTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.AutoMoqName) },
			{ "NetCoreMoqTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.MoqName) },
			{ "NetCoreNSubstituteTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.NSubstituteName) },
			{ "NetCoreNUnitTestCases", new SelfTestDetectionResult(TestFrameworks.NUnitName, MockFrameworks.MoqName) },
			{ "NetCoreSimpleStubsTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.SimpleStubsName) },
			{ "NetCoreVSRhinoMocksTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.RhinoMocksName) },
			{ "NoFrameworkTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.MoqName) },
			{ "NSubstituteTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.NSubstituteName) },
			{ "NUnitTestCases", new SelfTestDetectionResult(TestFrameworks.NUnitName, MockFrameworks.MoqName) },
			{ "NUnitUwpTestCases", new SelfTestDetectionResult(TestFrameworks.NUnitName, MockFrameworks.SimpleStubsName) },
			{ "SimpleStubsTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.SimpleStubsName) },
			{ "VSRhinoMocksTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.RhinoMocksName) },
			{ "VSTestCases", new SelfTestDetectionResult(TestFrameworks.VisualStudioName, MockFrameworks.MoqName) },
			{ "XUnitMoqTestCases", new SelfTestDetectionResult(TestFrameworks.XUnitName, MockFrameworks.MoqName) },
		};

		public void Clean(IList<Project> projects = null, bool save = false)
		{
			var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
			if (projects == null)
			{
				projects = SolutionUtilities.GetProjects(dte);
			}

			// First clean out cases in projects
			foreach (var project in projects)
			{
				if (project.Name.Contains("TestCases"))
				{
					// Delete Cases folder from project if it exists.
					foreach (ProjectItem item in project.ProjectItems)
					{
						if (item.Name == "Cases")
						{
							item.Delete();
							project.Save();
							break;
						}
					}

					// Delete folder if it existed but was not added to project.
					string projectFolder = Path.GetDirectoryName(project.FileName);
					string casesFolder = Path.Combine(projectFolder, "Cases");

					if (Directory.Exists(casesFolder))
					{
						Directory.Delete(casesFolder, recursive: true);
					}
				}
			}

			// Next, clean out any files left in SelfTestFiles\Actual
			string selfTestFilesDirectory = SolutionUtilities.GetSelfTestDirectoryFromSandbox(dte);
			string actualFolder = Path.Combine(selfTestFilesDirectory, "Actual");
			if (Directory.Exists(actualFolder))
			{
				Directory.Delete(actualFolder, recursive: true);
			}
		}

		public async Task<SelfTestRunResult> RunTestsAsync(DTE2 dte, Project classesProject, IList<Project> targetProjects)
		{
			var result = new SelfTestRunResult();

			this.Clean(targetProjects);

			SelfTestDetectionsResult detectionsResult = this.RunDetectionTests(targetProjects);
			result.DetectionFailures = detectionsResult.Failures;
			result.TotalDetectionsCount = detectionsResult.TotalCount;
			result.DetectionsSucceededCount = detectionsResult.SucceededCount;

			await this.GenerateTestFilesAsync(classesProject);

			SelfTestFileComparesResult fileComparesResult = this.AnalyzeFileResults(dte);

			result.FileFailures = fileComparesResult.Failures;
			result.FilesSucceededCount = fileComparesResult.SucceededCount;
			result.TotalFilesCount = fileComparesResult.TotalCount;

			return result;
		}

		public SelfTestDetectionsResult RunDetectionTests(IList<Project> targetProjects)
		{
			var result = new SelfTestDetectionsResult();

			var failures = new List<string>();

			foreach (Project testProject in targetProjects)
			{
				result.TotalCount++;

				TestFramework actualTestFramework = SolutionUtilities.FindTestFramework(testProject);
				MockFramework actualMockFramework = SolutionUtilities.FindMockFramework(testProject);

				SelfTestDetectionResult expectedDetectionResult;
				if (!ExpectedDetectionResults.TryGetValue(testProject.Name, out expectedDetectionResult))
				{
					failures.Add("Could not find expected detection results for project " + testProject.Name);
				}
				else if (expectedDetectionResult.TestFramework != actualTestFramework.Name)
				{
					failures.Add($"Expected {expectedDetectionResult.TestFramework} test framework for {testProject.Name} but got {actualTestFramework.Name}");
				}
				else if (expectedDetectionResult.MockFramework != actualMockFramework.Name)
				{
					failures.Add($"Expected {expectedDetectionResult.MockFramework} mock framework for {testProject.Name} but got {actualMockFramework.Name}");
				}
				else
				{
					result.SucceededCount++;
				}
			}

			result.Failures = failures;

			return result;
		}

		public async System.Threading.Tasks.Task GenerateTestFilesAsync(Project classesProject)
		{
			ProjectItem casesFolder = classesProject.ProjectItems.Item("Cases");

			var dte = (DTE2)ServiceProvider.GlobalProvider.GetService(typeof(DTE));
			string selfTestFilesDirectory = SolutionUtilities.GetSelfTestDirectoryFromSandbox(dte);
			string targetFolder = Path.Combine(selfTestFilesDirectory, "Actual");

			if (!Directory.Exists(targetFolder))
			{
				Directory.CreateDirectory(targetFolder);
			}

			var classNames = new List<string>();
			foreach (ProjectItem classItem in casesFolder.ProjectItems)
			{
				classNames.Add(Path.GetFileNameWithoutExtension(classItem.Name));
			}

			// testFW|mockFW|className
			var testDefinitions = new HashSet<string>();

			// First, iterate over all test frameworks with Moq and choose all classes
			MockFramework moqFramework = MockFrameworks.Get(MockFrameworks.MoqName);
			foreach (TestFramework testFramework in TestFrameworks.List)
			{
				foreach (string className in classNames)
				{
					TryAddTestDefinition(testDefinitions, testFramework, moqFramework, className);
				}
			}

			// Next, iterate over all mock frameworks with VS framework and choose all classes
			TestFramework vsFramework = TestFrameworks.Get(TestFrameworks.VisualStudioName);
			foreach (MockFramework mockFramework in MockFrameworks.List)
			{
				foreach (string className in classNames)
				{
					TryAddTestDefinition(testDefinitions, vsFramework, mockFramework, className);
				}
			}

			// Last, choose a single file and iterate over all mock frameworks and test frameworks
			string classWithGenericInterface = "ClassWithGenericInterface";
			foreach (TestFramework testFramework in TestFrameworks.List)
			{
				foreach (MockFramework mockFramework in MockFrameworks.List)
				{
					TryAddTestDefinition(testDefinitions, testFramework, mockFramework, classWithGenericInterface);
				}
			}

			foreach (string testDefinition in testDefinitions)
			{
				string[] parts = testDefinition.Split('|');

				string testFrameworkName = parts[0];
				string mockFrameworkName = parts[1];
				string className = parts[2];

				await this.GenerateTestFileAsync(classesProject, TestFrameworks.Get(testFrameworkName), MockFrameworks.Get(mockFrameworkName), className, targetFolder);
			}
		}

		public SelfTestFileComparesResult AnalyzeFileResults(DTE2 dte)
		{
			string selfTestFilesDirectory = SolutionUtilities.GetSelfTestDirectoryFromSandbox(dte);

			// Go over all "*TestCases" folders in actual results folder, dig into "Cases" directory
			int totalFiles = 0;
			int succeededCount = 0;

			var failures = new List<SelfTestFileFailure>();

			string actualFilesDirectory = Path.Combine(selfTestFilesDirectory, "Actual");
			string expectedFilesDirectory = Path.Combine(selfTestFilesDirectory, "Expected");

			var actualFilesDirectoryInfo = new DirectoryInfo(actualFilesDirectory);
			foreach (FileInfo actualFileInfo in actualFilesDirectoryInfo.GetFiles())
			{
				totalFiles++;

				string actualFilePath = actualFileInfo.FullName;
				string expectedFilePath = Path.Combine(expectedFilesDirectory, actualFileInfo.Name);

				string actualFileContents = File.ReadAllText(actualFilePath);

				if (File.Exists(expectedFilePath))
				{
					string expectedFileContents = File.ReadAllText(expectedFilePath);

					if (expectedFileContents == actualFileContents)
					{
						succeededCount++;
					}
					else
					{
						failures.Add(new SelfTestFileFailure
						{
							ExpectedFilePath = expectedFilePath,
							ActualFilePath = actualFilePath,
							ExpectedContents = expectedFileContents,
							ActualContents = actualFileContents
						});
					}
				}
				else
				{
					// Expected file does not exist
					failures.Add(new SelfTestFileFailure
					{
						ExpectedFilePath = expectedFilePath,
						ActualFilePath = actualFilePath,
						ExpectedContents = null,
						ActualContents = actualFileContents
					});
				}
			}

			return new SelfTestFileComparesResult
			{
				TotalCount = totalFiles,
				SucceededCount = succeededCount,
				Failures = failures
			};
		}

		private static void TryAddTestDefinition(HashSet<string> definitions, TestFramework testFramework, MockFramework mockFramework, string className)
		{
			string definition = CreateTestDefinition(testFramework, mockFramework, className);
			if (!definitions.Contains(definition))
			{
				definitions.Add(definition);
			}
		}

		private static string CreateTestDefinition(TestFramework testFramework, MockFramework mockFramework, string className)
		{
			return $"{testFramework.Name}|{mockFramework.Name}|{className}";
		}

		public async System.Threading.Tasks.Task GenerateTestFileAsync(Project classesProject, TestFramework testFramework, MockFramework mockFramework, string className, string targetFolder)
		{
			// TODO: Calc targetFilePath from all the inputs
			string targetFileName = $"{testFramework.Name}_{mockFramework.Name}_{className}.cs";
			string targetFilePath = Path.Combine(targetFolder, targetFileName);

			ProjectItem casesFolder = classesProject.ProjectItems.Item("Cases");
			ProjectItem classToTest = casesFolder.ProjectItems.Item(className + ".cs");

			var testGenerationService = new TestGenerationService();
			await testGenerationService.GenerateUnitTestFileAsync(new ProjectItemSummary(classToTest), targetFilePath, "UnitTestBoilerplate.SelfTest", testFramework, mockFramework);
		}
	}
}
