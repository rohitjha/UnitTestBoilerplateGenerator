﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnitTestBoilerplate.Model;

namespace UnitTestBoilerplate
{
	public class DefaultTemplateGenerator
	{
		private int indentLevel;

		private StringBuilder template;

		public string Get(TestFramework testFramework, MockFramework mockFramework)
		{
			this.indentLevel = 0;

			this.template = new StringBuilder();

			// Using statements
			this.AppendLineIndented("$UsingStatements$");
			this.AppendLineIndented();

			// Namespace
			this.AppendLineIndented("namespace $Namespace$");
			this.AppendLineIndented("{");
			this.indentLevel++;

			// Test class attribute
			if (!string.IsNullOrEmpty(testFramework.TestClassAttribute))
			{
				this.AppendLineIndented($"[{testFramework.TestClassAttribute}]");
			}

			// Test class declaration
			this.AppendIndent();
			this.template.Append("public class $ClassName$Tests");
			if (mockFramework.HasTestCleanup && testFramework.TestCleanupStyle == TestCleanupStyle.Disposable)
			{
				this.template.Append(" : IDisposable");
			}

			this.template.AppendLine();
			this.AppendLineIndented("{");
			this.indentLevel++;

			// Test class start code
			if (!string.IsNullOrEmpty(mockFramework.ClassStartCode))
			{
				this.AppendLineIndented(mockFramework.ClassStartCode);
				this.AppendLineIndented();
			}

			if (mockFramework.HasMockFields)
			{
				// Mock field declaration
				this.AppendLineIndented("$MockFieldDeclarations$");
				this.AppendLineIndented();

				// Test initialize
				switch (testFramework.TestInitializeStyle)
				{
					case TestInitializeStyle.Constructor:
						this.AppendLineIndented("public $ClassName$Tests()");

						break;
					case TestInitializeStyle.AttributedMethod:
						this.AppendLineIndented($"[{testFramework.TestInitializeAttribute}]");
						this.AppendLineIndented($"public void {testFramework.TestInitializeAttribute}()");

						break;
					default:
						throw new ArgumentOutOfRangeException(nameof(testFramework));
				}

				this.AppendLineIndented("{");
				this.indentLevel++;

				if (!string.IsNullOrEmpty(mockFramework.InitializeStartCode))
				{
					this.AppendLineIndented(mockFramework.InitializeStartCode);
					this.AppendLineIndented();
				}

				this.AppendLineIndented("$MockFieldInitializations$");

				this.indentLevel--;
				this.AppendLineIndented("}");
				this.AppendLineIndented();
			}

			// Test cleanup
			if (mockFramework.HasTestCleanup)
			{
				switch (testFramework.TestCleanupStyle)
				{
					case TestCleanupStyle.Disposable:
						this.AppendLineIndented("public void Dispose()");

						break;
					case TestCleanupStyle.AttributedMethod:
						this.AppendLineIndented($"[{testFramework.TestCleanupAttribute}]");
						this.AppendLineIndented($"public void {testFramework.TestCleanupAttribute}()");

						break;
					default:
						throw new ArgumentOutOfRangeException(nameof(testFramework));
				}

				this.AppendLineIndented("{");
				this.indentLevel++;

				this.AppendLineIndented(mockFramework.TestCleanupCode);

				this.indentLevel--;
				this.AppendLineIndented("}");
				this.AppendLineIndented();
			}

			// Test method
			this.AppendLineIndented($"[{testFramework.TestMethodAttribute}]");
			this.AppendLineIndented("public void TestMethod1()");
			this.AppendLineIndented("{");
			this.indentLevel++;

			this.AppendLineIndented("// Arrange");
			if (!string.IsNullOrEmpty(mockFramework.TestArrangeCode))
			{
				this.AppendLineIndented(mockFramework.TestArrangeCode);
			}

			this.AppendLineIndented(); // Blank line for users to put in their own arrange code
			this.AppendLineIndented(); // Separator

			this.AppendLineIndented("// Act");
			switch (mockFramework.TestedObjectCreationStyle)
			{
				case TestedObjectCreationStyle.HelperMethod:
					this.AppendLineIndented("$ClassName$ $ClassNameShort.CamelCase$ = this.Create$ClassNameShort$();");

					break;
				case TestedObjectCreationStyle.DirectCode:
					this.AppendLineIndented(mockFramework.TestedObjectCreationCode);

					break;
				default:
					throw new ArgumentOutOfRangeException();
			}

			this.AppendLineIndented(); // Blank line for users to put in their own act code
			this.AppendLineIndented(); // Separator

			this.AppendLineIndented("// Assert");
			this.AppendLineIndented(); // Blank line for users to put in their own assert code

			this.indentLevel--;
			this.AppendLineIndented("}");

			// Helper method to create tested object
			if (mockFramework.TestedObjectCreationStyle == TestedObjectCreationStyle.HelperMethod)
			{
				this.AppendLineIndented();
				this.AppendLineIndented("private $ClassName$ Create$ClassNameShort$()");
				this.AppendLineIndented("{");
				this.indentLevel++;
				this.AppendLineIndented("return $ExplicitConstructor$;");
				this.indentLevel--;
				this.AppendLineIndented("}");

			}

			// Test class/namespace end
			this.indentLevel--;
			this.AppendLineIndented("}");
			this.indentLevel--;
			this.AppendLineIndented("}");

			return this.template.ToString();
		}

		public void AppendIndent()
		{
			for (int i = 0; i < this.indentLevel; i++)
			{
				this.template.Append('\t');
			}
		}

		public void AppendLineIndented(string line)
		{
			this.AppendIndent();
			this.template.AppendLine(line);
		}

		public void AppendLineIndented()
		{
			this.AppendIndent();
			this.template.AppendLine(string.Empty);
		}
	}
}
