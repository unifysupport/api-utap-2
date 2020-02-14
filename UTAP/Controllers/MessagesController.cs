using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Threading.Tasks.Dataflow;

namespace UTAP.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class MessagesController : ControllerBase
    {
        [HttpPost]
        public IActionResult Post([FromBody] SingleMessage message, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                if (message.Body.MessageId == Guid.Empty)
                {
                    Guid messageId = Guid.NewGuid();
                    message.Body.MessageId = messageId; 
                }
                arbiter.StorePendingMessage(message);
                arbiter._queue.Post(message);
                return Ok(message.Body.MessageId);
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
