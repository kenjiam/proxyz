using Proxyz.Core.Tests;

var suite = new TestSuite();
SettingsTests.Register(suite);
RuleTests.Register(suite);
ControllerTests.Register(suite);
StorageTests.Register(suite);
return suite.Run();
