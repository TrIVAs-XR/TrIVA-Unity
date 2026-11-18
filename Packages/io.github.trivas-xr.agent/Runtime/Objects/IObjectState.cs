using System;
using Newtonsoft.Json.Linq;

namespace TrIVA.Objects
{
    /// <summary>
    /// Implemented by a scenario's component next to a ReferableObject to add what the object currently is to its description, such as what a flask holds.
    /// </summary>
    public interface IObjectState
    {
        /// <summary>
        /// Raised when what Describe adds has changed.
        /// </summary>
        event Action StateChanged;

        /// <summary>
        /// Adds the object's current state to its description, next to the ID and kind already in it.
        /// </summary>
        void Describe(JObject description);
    }
}
