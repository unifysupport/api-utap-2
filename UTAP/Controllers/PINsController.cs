using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using UTAP;
using Microsoft.AspNetCore.Authorization;

namespace UTAP.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PINsController : ControllerBase
    {
        [HttpGet]
        public ActionResult<IEnumerable<PIN>> GetAllPINs([FromServices] SMSArbiter arbiter)
        {
            return Ok(arbiter._DBManager.PINList);
        }

        [HttpGet("connected")]
        public ActionResult isConnected() { return Ok(); }
        

        [HttpGet("{number}")]
        public ActionResult<string> GetSpecificPIN(int number, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                var PINMatch = arbiter._DBManager.PINList.SingleOrDefault(PIN => PIN.Number == number);
                if (PINMatch == null) return NotFound();
                return Ok(PINMatch);
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpGet("{number}/PANs")]
        public ActionResult<string> GetPANsByPIN(int number, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                var PINMatch = arbiter._DBManager.PINList.SingleOrDefault(PIN => PIN.Number == number);
                if (PINMatch == null) return NotFound();
                var PANMatches = arbiter._DBManager.PANList.Where(pan => pan.PIN == number);
                return Ok(PANMatches);
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpGet("{number}/conversations")]
        public ActionResult<string> GetConversationsByPIN(int number, [FromQuery] DateTime? FromDate, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                var PINMatch = arbiter._DBManager.PINList.SingleOrDefault(PIN => PIN.Number == number);
                if (PINMatch == null) return NotFound();
                var PINConversations = arbiter.GetAllConversations(number, FromDate);

                return Ok(PINConversations);
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpGet("{Number}/pan/{PAN}")]
        public ActionResult<string> GetConversation(int Number, string PAN, [FromQuery] DateTime? BeforeDate, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                var PINMatch = arbiter._DBManager.PINList.SingleOrDefault(PIN => PIN.Number == Number);
                if (PINMatch == null) return NotFound();
                var Conversation = arbiter.GetConversation(Number, PAN, BeforeDate);
                return Ok(Conversation);
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }

        [HttpPost("{PIN}/pan/{PAN}/read")]
        public IActionResult PostReadStatus(int PIN, string PAN, [FromServices] SMSArbiter arbiter)
        {
            try
            {
                var PINMatch = arbiter._DBManager.PINList.SingleOrDefault(P => P.Number == PIN);
                if (PINMatch == null) return NotFound();
                arbiter._DBManager.Enqueue(string.Format("UPDATE allowed_phone_numbers SET ConversationRead = 1 WHERE PIN = {0} AND p_number = '{1}'", PIN, PAN));
                return Ok();
            }
            catch
            {
                return StatusCode(StatusCodes.Status500InternalServerError);
            }
        }
    }
}
