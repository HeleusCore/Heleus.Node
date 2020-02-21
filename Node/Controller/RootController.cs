using System;
using Heleus.Node.Views;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class RootController : HeleusController

    {
        public RootController(Node node) : base(node)
        {
        }

        [Route("")]
        public ActionResult Home()
        {
            return Redirect("static/json/network/nodeinfo");
        }

        [Route("error")]
        public ActionResult Error()
        {
            return new JsonResult(ErrorView.Ooopsi);
        }

        [Route("error/{code:int}")]
        public JsonResult Error(int code)
        {
            if (code == 404)
                return new JsonResult(ErrorView.NotFound) { StatusCode = code };

            return new JsonResult(new ErrorView(code, ErrorView.Ooopsi.error)) { StatusCode = code };
        }
    }
}
