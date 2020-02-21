using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Heleus.Base;
using Heleus.Manager;
using Heleus.Transactions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Heleus.Node.Controller
{
    public class AttachementsController : HeleusController
    {
        public AttachementsController(Node node) : base(node)
        {
        }

        [Route("static/datachain/{chainid:int}/{chainindex:long}/attachements/{attachementkey:int}/{transactionid:long}_{name}")]
        public ActionResult GetAttachement(int chainid, long chainindex, int attachementkey, long transactionid, string name)
        {
            if (!AttachementItem.IsNameValid(name))
                return BadRequest();

            var path = _node.AttachementManager.GetAttachementPath(chainid, (uint)chainindex, attachementkey, transactionid, name);

            return PhysicalFile(path, "application/octet-stream", AttachementManager.GetAttachementFileName(transactionid, name));
        }

        static string GetResultCode(TransactionResultTypes resultType, long userCode)
        {
            return ((int)resultType).ToString() + "," + userCode;
        }

        [HttpPost("dynamic/datachain/{chainid:int}/{chainindex:long}/attachements/{attachementkey:int}/upload")]
        public async Task<IActionResult> AttachementsUpload(int chainid, long chainIndex, int attachementkey, List<IFormFile> files)
        {
            var userCode = 0L;

            Attachements attachements = null;
            try
            {
                Request.Headers.TryGetValue("X-Attachements", out var attachementsHeader);
                var attachementsData = Convert.FromBase64String(attachementsHeader.ToString());
                attachements = new Attachements(new Unpacker(attachementsData));
            }
            catch(Exception ex)
            {
                Log.IgnoreException(ex, this);
            }

            if (attachements == null || attachements.Items.Count != files.Count || attachements.ChainId != chainid || attachements.ChainIndex != (uint)chainIndex)
                return BadRequest(GetResultCode(TransactionResultTypes.AttachementsInvalid, userCode));

            var attachementManager = _node.AttachementManager;
            var info = attachementManager.GetAttachementInfo(attachements.ChainId, attachements.ChainIndex, attachements.Token);
            if (info == null || !info.IsValid(attachements) || info.Expired || info.State != AttachementInfoState.Requested)
                return BadRequest(GetResultCode(TransactionResultTypes.AttachementsInvalid, userCode));

            info.SetState(AttachementInfoState.Uploading);

            try
            {
                var tempFiles = new List<AttachementTempFile>();
                foreach (var formFile in files)
                {
                    if (formFile.Length > 0)
                    {
                        var stream = formFile.OpenReadStream();
                        var item = info.GetAttachementItem(formFile.FileName);

                        if(stream != null && item != null)
                            tempFiles.Add(new AttachementTempFile(item, stream));
                    }
                }

                if (tempFiles.Count == info.Attachements.Items.Count)
                {
                    var copyResult = await attachementManager.CheckAndCacheAttachements(info, tempFiles);
                    userCode = copyResult.UserCode;

                    if (copyResult.Result == TransactionResultTypes.Ok)
                    {
                        info.SetState(AttachementInfoState.Uploaded);
                        return Ok(GetResultCode(TransactionResultTypes.Ok, userCode));
                    }

                    info.SetState(AttachementInfoState.Requested);
                    return BadRequest(GetResultCode(TransactionResultTypes.ChainServiceErrorResponse, userCode));
                }
            }
            catch (Exception ex)
            {
                Log.IgnoreException(ex, this);
            }

            info.SetState(AttachementInfoState.Requested);
            return BadRequest(GetResultCode(TransactionResultTypes.AttachementsUploadFailed, userCode));
        }
    }
}
