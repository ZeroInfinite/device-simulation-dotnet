// Copyright (c) Microsoft. All rights reserved.

using System.Collections.Generic;
using Newtonsoft.Json;

namespace Microsoft.Azure.IoTSolutions.DeviceSimulation.WebService.v1.Models
{
    public class SimulationListApiModel
    {
        [JsonProperty(PropertyName = "Items")]
        public List<SimulationApiModel> Items { get; set; }

        [JsonProperty(PropertyName = "$metadata")]
        public Dictionary<string, string> Metadata => new Dictionary<string, string>
        {
            { "$type", "SimulationList;" + Version.Number },
            { "$uri", "/" + Version.Path + "/simulations" }
        };

        public SimulationListApiModel()
        {
            this.Items = new List<SimulationApiModel>();
        }

        /// <summary>Map a service model to the corresponding API model</summary>
        public SimulationListApiModel(IEnumerable<Services.Models.Simulation> simulations)
        {
            this.Items = new List<SimulationApiModel>();
            foreach (var x in simulations) this.Items.Add(new SimulationApiModel(x));
        }
    }
}