using System;
using System.IO;
static class IconBuilder { static void Main(string[] args) {using(var icon=Theme.Icon(Theme.Accent))using(var file=File.Create(args[0]))icon.Save(file);} }
