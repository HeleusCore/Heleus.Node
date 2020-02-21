using System;

namespace Heleus.Node.Views
{
    public class ErrorView
    {
        public readonly int errorcode;
        public readonly string error;

        public ErrorView(int code, string message)
        {
            errorcode = code;
            error = message;
        }

        public static readonly ErrorView BadRequest = new ErrorView(400, "You failed.");
        public static readonly ErrorView NotFound = new ErrorView(404, "Not found.");
        public static readonly ErrorView Ooopsi = new ErrorView(500, "Ooopsi, something went wrong.");
    }
}
