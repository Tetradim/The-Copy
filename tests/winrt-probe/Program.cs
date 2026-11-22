using System.Security.AccessControl;
using System.Security.Principal;
var path=Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
var rules=new DirectoryInfo(path).GetAccessControl().GetAccessRules(true,true,typeof(SecurityIdentifier));
Console.WriteLine(rules.Count);
