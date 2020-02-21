using System;
using Heleus.Base;
using Heleus.Chain;
using Heleus.Node.Views;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class HeleusController : ControllerBase, ILogger
    {
        protected readonly Node _node;

        public string LogName => GetType().Name;

        public HeleusController(Node node)
        {
            _node = node;
        }

        public ActionResult NotFoundJson()
        {
            return new JsonResult(ErrorView.NotFound) { StatusCode = 404 };
        }

        public ActionResult BadRequestJson()
        {
            return new JsonResult(ErrorView.BadRequest) { StatusCode = 400 };
        }

        static string _coreChainName = ChainType.Core.ToString().ToLower();
        static string _serviceChainName = ChainType.Service.ToString().ToLower();
        static string _dataChainName = ChainType.Data.ToString().ToLower();
        static string _maintainChainName = ChainType.Maintain.ToString().ToLower();

        public bool GetChainType(string chainTypeName, out ChainType chainType)
        {
            if(chainTypeName == _dataChainName)
            {
                chainType = ChainType.Data;
                return true;
            }

            if(chainTypeName == _coreChainName)
            {
                chainType = ChainType.Core;
                return true;
            }

            if(chainTypeName == _serviceChainName)
            {
                chainType = ChainType.Service;
                return true;
            }

            if(chainTypeName == _maintainChainName)
            {
                chainType = ChainType.Maintain;
                return true;
            }

            throw new Exception($"ChainType {chainTypeName} not found.");
        }
    }
}
