﻿using System.ComponentModel;
using System.Reflection;

namespace BrightstarDB.CodeGeneration.Tests
{
    using System;
    using System.IO;
    using System.Linq;
    using System.Text;
    using System.Threading.Tasks;
    using BrightstarDB.CodeGeneration;
    using Microsoft.CodeAnalysis;
    using Microsoft.CodeAnalysis.Text;
    using NUnit.Framework;

    [TestFixture]
    public class GeneratorTests
    {
        [Test]
        [TestCase("EmptyEntity", false, Language.CSharp)]
        [TestCase("EmptyEntity", false, Language.VisualBasic)]
        [TestCase("EmptyEntity", true, Language.CSharp)]
        [TestCase("EmptyEntity", true, Language.VisualBasic)]
        [TestCase("InternalEntity", false, Language.CSharp)]
        [TestCase("InternalEntity", true, Language.CSharp)]
        [TestCase("IdentifierPrecedence", false, Language.CSharp)]
        [TestCase("IdentifierPrecedence", false, Language.VisualBasic)]
        [TestCase("SupportedScalarPropertyTypes", false, Language.CSharp)]
        [TestCase("SupportedScalarPropertyTypes", false, Language.VisualBasic)]
        [TestCase("Relationships", false, Language.CSharp)]
        [TestCase("Relationships", false, Language.VisualBasic)]
        [TestCase("AttributePropagation", false, Language.CSharp)]
        [TestCase("AttributePropagation", false, Language.VisualBasic)]
        public async Task TestCodeGeneration(string resourceBaseName, bool internalEntityClasses, Language language)
        {
            var inputResourceName = "BrightstarDB.CodeGeneration.Tests.GeneratorTestsResources." + resourceBaseName + "Input_" + language.ToString() + ".txt";
            var outputResourceName = "BrightstarDB.CodeGeneration.Tests.GeneratorTestsResources." + resourceBaseName + "Output_" + language.ToString() + ".txt";

            using (var inputStream = this.GetType().Assembly.GetManifestResourceStream(inputResourceName))
            using (var outputStream = this.GetType().Assembly.GetManifestResourceStream(outputResourceName))
            using (var outputStreamReader = new StreamReader(outputStream))
            {
                var workspace = new AdhocWorkspace();
                var projectId = ProjectId.CreateNewId();
                var versionStamp = VersionStamp.Create();
                var projectInfo = ProjectInfo.Create(
                    projectId,
                    versionStamp,
                    "AdhocProject",
                    "AdhocProject",
                    language.ToSyntaxGeneratorLanguageName(),
                    metadataReferences: new[]
                    {
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(Uri).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(BrightstarException).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(BrowsableAttribute).Assembly.Location),
                        MetadataReference.CreateFromFile(Assembly.Load("netstandard, Version=2.0.0.0").Location),
                        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime, Version=0.0.0.0").Location)
            });
                var project = workspace.AddProject(projectInfo);
                workspace.AddDocument(projectId, "Source.cs", SourceText.From(inputStream));
                var solution = workspace.CurrentSolution;
                var brightstarAssemblyPath = typeof(BrightstarException).Assembly.Location;
                var results = await Generator
                    .GenerateAsync(
                        language,
                        solution,
                        "BrightstarDB.CodeGeneration.Tests",
                        interfacePredicate: x => true,
                        entityAccessibilitySelector: internalEntityClasses ? (Func<INamedTypeSymbol, Accessibility>) Generator.InteralyEntityAccessibilitySelector : Generator.DefaultEntityAccessibilitySelector,
                        brightstarAssemblyPath:brightstarAssemblyPath);
                var result = results
                    .Aggregate(
                        new StringBuilder(),
                        (current, next) => current.AppendLine(next.ToString()),
                        x => x.ToString());

                var expectedCode = outputStreamReader.ReadToEnd();

                // make sure version changes don't break the tests
                expectedCode = expectedCode.Replace("$VERSION$", typeof(BrightstarException).Assembly.GetName().Version.ToString());
                expectedCode = expectedCode.Replace("$ENTITYACCESS$",
                    language == Language.VisualBasic
                        ? (internalEntityClasses ? "Friend" : "Public")
                        : (internalEntityClasses ? "internal" : "public"));

                //// useful when converting generated code to something that can be pasted into an expectation file
                //var sanitisedResult = result.Replace("1.10.0.0", "$VERSION$");
                //System.Diagnostics.Debug.WriteLine(sanitisedResult);

                Assert.AreEqual(expectedCode.Replace("\r\n", "\n"), result.Replace("\r\n", "\n"));
            }
        }

        [Test]
        [TestCase("InvalidIdType", Language.CSharp, "The property 'BrightstarDB.CodeGeneration.Tests.IInvalidIdType.Id' must be of type string to be used as the identity property for an entity. If this property is intended to be the identity property for the entity please change its type to string. If it is not intended to be the identity property, either rename this property or create an identity property and decorate it with the [BrightstarDB.EntityFramework.IdentifierAttribute] attribute.")]
        [TestCase("InvalidIdType", Language.VisualBasic, "The property 'ReadOnly Property Id As Integer' must be of type String to be used as the identity property for an entity. If this property is intended to be the identity property for the entity please change its type to String. If it is not intended to be the identity property, either rename this property or create an identity property and decorate it with the [BrightstarDB.EntityFramework.IdentifierAttribute] attribute.")]
        [TestCase("IdWithSetter", Language.CSharp, "The property 'BrightstarDB.CodeGeneration.Tests.IIdWithSetter.Id' must not have a setter to be used as the identity property for an entity. If this property is intended to be the identity property for the entity please remove the setter. If it is not intended to be the identity property, either rename this property or create an identity propertyn and decorate it with the [BrightstarDB.EntityFramework.IdentifierAttribute] attribute.")]
        [TestCase("IdWithSetter", Language.VisualBasic, "The property 'Property Id As String' must not have a setter to be used as the identity property for an entity. If this property is intended to be the identity property for the entity please remove the setter. If it is not intended to be the identity property, either rename this property or create an identity propertyn and decorate it with the [BrightstarDB.EntityFramework.IdentifierAttribute] attribute.")]
        [TestCase("PropertyWithUnsupportedType", Language.CSharp, "Invalid property: BrightstarDB.CodeGeneration.Tests.IPropertyWithUnsupportedType.Property - the property type System.Action is not supported by Entity Framework.")]
        [TestCase("PropertyWithUnsupportedType", Language.VisualBasic, "Invalid property: Property _Property As System.Action - the property type System.Action is not supported by Entity Framework.")]
        [TestCase("InvalidInversePropertyName", Language.CSharp, "Invalid BrightstarDB.EntityFramework.InversePropertyAttribute attribute on property BrightstarDB.CodeGeneration.Tests.IInvalidInversePropertyName_B.A. A property named 'B' cannot be found on the target interface type BrightstarDB.CodeGeneration.Tests.IInvalidInversePropertyName_A.")]
        [TestCase("InvalidInversePropertyName", Language.VisualBasic, "Invalid BrightstarDB.EntityFramework.InversePropertyAttribute attribute on property Property A As BrightstarDB.CodeGeneration.Tests.IInvalidInversePropertyName_A. A property named 'B' cannot be found on the target interface type BrightstarDB.CodeGeneration.Tests.IInvalidInversePropertyName_A.")]
        public async Task TestErrorConditions(string resourceBaseName, Language language, string expectedErrorMessage)
        {
            var inputResourceName = "BrightstarDB.CodeGeneration.Tests.GeneratorTestsResources." + resourceBaseName + "Input_" + language.ToString() + ".txt";

            using (var inputStream = this.GetType().Assembly.GetManifestResourceStream(inputResourceName))
            {
                var workspace = new AdhocWorkspace();
                var projectId = ProjectId.CreateNewId();
                var versionStamp = VersionStamp.Create();
                var projectInfo = ProjectInfo.Create(
                    projectId,
                    versionStamp,
                    "AdhocProject",
                    "AdhocProject",
                    language.ToSyntaxGeneratorLanguageName(),
                    metadataReferences: new[]
                    {
                        MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(Uri).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(BrightstarException).Assembly.Location),
                        MetadataReference.CreateFromFile(typeof(BrowsableAttribute).Assembly.Location),
                        MetadataReference.CreateFromFile(Assembly.Load("netstandard, Version=2.0.0.0").Location),
                        MetadataReference.CreateFromFile(Assembly.Load("System.Runtime, Version=0.0.0.0").Location)
                    });
                var project = workspace.AddProject(projectInfo);
                workspace.AddDocument(projectId, "Source.cs", SourceText.From(inputStream));
                var solution = workspace.CurrentSolution;
                var brightstarAssemblyPath = typeof(BrightstarException).Assembly.Location;
                try
                {
                    var results = await Generator
                        .GenerateAsync(
                            language,
                            solution,
                            "BrightstarDB.CodeGeneration.Tests",
                            interfacePredicate: x => true,
                            brightstarAssemblyPath:brightstarAssemblyPath);

                    Assert.Fail("No exception was thrown during code generation.");
                }
                catch (Exception ex)
                {
                    Assert.AreEqual(expectedErrorMessage, ex.Message);
                }
            }
        }
    }
}