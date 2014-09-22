using System;

namespace LogGrok.Shell.Starter
{
    static class EntryPoint
    {
	    [STAThread]
	    public static void Main()
	    {
		    new App().Run();
	    }
    }
}
