﻿// Copyright (c) Microsoft. All rights reserved.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.AzureManagementAdapter;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Clustering;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Concurrency;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Diagnostics;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Models;
using Microsoft.Azure.IoTSolutions.DeviceSimulation.Services.Runtime;

namespace Microsoft.Azure.IoTSolutions.DeviceSimulation.PartitioningAgent
{
    public interface IPartitioningAgent
    {
        Task StartAsync();
        void Stop();
    }

    public class Agent : IPartitioningAgent
    {
        private const int DEFAULT_NODE_COUNT = 1;
        private readonly IClusterNodes clusterNodes;
        private readonly IDevicePartitions partitions;
        private readonly ISimulations simulations;
        private readonly IThreadWrapper thread;
        private readonly IFactory factory;
        private readonly ILogger log;
        private readonly IAzureManagementAdapterClient azureManagementAdapter;
        private readonly IClusteringConfig clusteringConfig;
        private readonly int checkIntervalMsecs;
        private int currentNodeCount;

        private bool running;

        public Agent(
            IClusterNodes clusterNodes,
            IDevicePartitions partitions,
            ISimulations simulations,
            IThreadWrapper thread,
            IClusteringConfig clusteringConfig,
            IFactory factory,
            ILogger logger,
            IAzureManagementAdapterClient azureManagementAdapter)
        {
            this.clusterNodes = clusterNodes;
            this.partitions = partitions;
            this.simulations = simulations;
            this.thread = thread;
            this.factory = factory;
            this.log = logger;
            this.azureManagementAdapter = azureManagementAdapter;
            this.clusteringConfig = clusteringConfig;
            this.checkIntervalMsecs = clusteringConfig.CheckIntervalMsecs;
            this.running = false;
            this.currentNodeCount = DEFAULT_NODE_COUNT;
        }

        public async Task StartAsync()
        {
            this.log.Info("Partitioning agent started", () => new { Node = this.clusterNodes.GetCurrentNodeId() });

            this.running = true;

            // Repeat until the agent is stopped
            while (this.running)
            {
                await this.clusterNodes.KeepAliveNodeAsync();

                // Only one process in the network becomes a master, and the master
                // is responsible for running tasks not meant to run in parallel, like
                // creating devices and partitions, and other tasks
                var isMaster = await this.clusterNodes.SelfElectToMasterNodeAsync();
                if (isMaster)
                {
                    // Reload all simulations to have fresh status and discover new simulations
                    IList<Simulation> simulations = (await this.simulations.GetListAsync());

                    IList<Simulation> activeSimulations = simulations
                        .Where(x => x.IsActiveNow).ToList();
                    this.log.Debug("Active simulations loaded", () => new { activeSimulations.Count });

                    IList<Simulation> deletionRequiredSimulations = simulations
                        .Where(x => x.DeviceDeletionRequired).ToList();
                    this.log.Debug("InActive simulations loaded", () => new { deletionRequiredSimulations.Count });

                    await this.clusterNodes.RemoveStaleNodesAsync();

                    // Scale nodes in Vmss
                    await this.ScaleVmssNodes(activeSimulations);

                    // Create IoTHub devices for all the active simulations
                    await this.CreateDevicesAsync(activeSimulations);

                    // Delete IoTHub devices for inactive simulations
                    await this.DeleteDevicesAsync(deletionRequiredSimulations);

                    // Create and delete partitions
                    await this.CreatePartitionsAsync(activeSimulations);
                    await this.DeletePartitionsAsync(activeSimulations);
                }

                // Sleep some seconds before checking for new simulations (by default 15 seconds)
                this.thread.Sleep(this.checkIntervalMsecs);
            }
        }

        public void Stop()
        {
            this.running = false;
        }
                
        private async Task ScaleVmssNodes(IList<Simulation> activeSimulations)
        {
            // Default node count is 1
            var nodeCount = DEFAULT_NODE_COUNT;
            var maxDevicesPerNode = this.clusteringConfig.MaxDevicesPerNode;

            if (activeSimulations.Count > 0)
            {
                var models = new List<Simulation.DeviceModelRef>();
                var customDevices = 0;

                foreach (var simulation in activeSimulations)
                {
                    // Loop through all the device models used in the simulation
                    models = (from model in simulation.DeviceModels where model.Count > 0 select model).ToList();

                    // Count total custom devices
                    customDevices += simulation.CustomDevices.Count;
                }

                // Calculate the total number of devices
                var totalDevices = models.Sum(model => model.Count) + customDevices;

                // Calculate number of nodes required
                nodeCount = maxDevicesPerNode > 0 ? (int)Math.Ceiling((double)totalDevices / maxDevicesPerNode) : DEFAULT_NODE_COUNT;
            }

            if (this.currentNodeCount != nodeCount)
            {
                // Send a request to update vmss auto scale settings to create vm instances
                // TODO: when devices are added or removed, the number of VMs might need an update
                await this.azureManagementAdapter.CreateOrUpdateVmssAutoscaleSettingsAsync(nodeCount);

                this.currentNodeCount = nodeCount;
            }
        }

        private async Task DeleteDevicesAsync(IList<Simulation> deletionRequiredSimulations)
        {
            if (deletionRequiredSimulations.Count == 0) return;

            foreach (var simulation in deletionRequiredSimulations)
            {
                await this.DeleteIoTHubDevicesAsync(simulation);
            }
        }

        private async Task CreateDevicesAsync(IList<Simulation> activeSimulations)
        {
            if (activeSimulations.Count == 0) return;

            var simulationsWithDevicesToCreate = activeSimulations.Where(x => x.DeviceCreationRequired).ToList();

            if (simulationsWithDevicesToCreate.Count == 0)
            {
                this.log.Debug("No simulations require device creation");
                return;
            }

            foreach (var simulation in simulationsWithDevicesToCreate)
            {
                await this.CreateIoTHubDevicesAsync(simulation);
            }
        }

        private async Task DeleteIoTHubDevicesAsync(Simulation simulation)
        {
            var deletionFailed = false;

            // Check if the device deletion is complete
            if (simulation.DevicesDeletionStarted)
            {
                this.log.Info("Checking if the device deletion is complete...", () => new { SimulationId = simulation.Id });

                // TODO: optimize, we can probably cache this instance
                // e.g. to avoid fetching the conn string from storage
                var deviceService = this.factory.Resolve<IDevices>();
                await deviceService.InitAsync();

                if (await deviceService.IsJobCompleteAsync(simulation.DeviceDeletionJobId, () => { deletionFailed = true; }))
                {
                    this.log.Info("All devices have been deleted, updating the simulation record", () => new { SimulationId = simulation.Id });

                    if (await this.simulations.TryToSetDeviceDeletionCompleteAsync(simulation.Id))
                    {
                        this.log.Debug("Simulation record updated");
                    }
                    else
                    {
                        this.log.Warn("Failed to update the simulation record, will retry later");
                    }
                }
                else
                {
                    this.log.Info(deletionFailed
                            ? "Device deletion failed. Will retry."
                            : "Device deletion is still in progress",
                        () => new { SimulationId = simulation.Id });
                }
            }

            // Start the job to delete the devices
            if ((!simulation.DevicesDeletionStarted && !simulation.DevicesDeletionComplete) || deletionFailed)
            {
                this.log.Debug("Starting devices creation", () => new { SimulationId = simulation.Id });

                // TODO: optimize, we can probably cache this instance
                // e.g. to avoid fetching the conn string from storage
                var deviceService = this.factory.Resolve<IDevices>();
                await deviceService.InitAsync();

                if (await this.simulations.TryToStartDevicesDeletionAsync(simulation.Id, deviceService))
                {
                    this.log.Info("Device deletion started");
                }
                else
                {
                    this.log.Warn("Failed to start device deletion, will retry later");
                }
            }
        }

        private async Task CreateIoTHubDevicesAsync(Simulation simulation)
        {
            var creationFailed = false;

            // Check if the device creation is complete
            if (simulation.DevicesCreationStarted)
            {
                this.log.Info("Checking if the device creation is complete...", () => new { SimulationId = simulation.Id });

                // TODO: optimize, we can probably cache this instance
                // e.g. to avoid fetching the conn string from storage
                var deviceService = this.factory.Resolve<IDevices>();
                await deviceService.InitAsync();

                if (await deviceService.IsJobCompleteAsync(simulation.DeviceCreationJobId, () => { creationFailed = true; }))
                {
                    this.log.Info("All devices have been created, updating the simulation record", () => new { SimulationId = simulation.Id });

                    if (await this.simulations.TryToSetDeviceCreationCompleteAsync(simulation.Id))
                    {
                        this.log.Debug("Simulation record updated");
                    }
                    else
                    {
                        this.log.Warn("Failed to update the simulation record, will retry later");
                    }
                }
                else
                {
                    this.log.Info(creationFailed
                            ? "Device creation failed. Will retry."
                            : "Device creation is still in progress",
                        () => new { SimulationId = simulation.Id });
                }
            }

            // Start the job to import the devices
            if ((!simulation.DevicesCreationStarted && !simulation.DevicesCreationComplete) || creationFailed)
            {
                this.log.Debug("Starting devices creation", () => new { SimulationId = simulation.Id });

                // TODO: optimize, we can probably cache this instance
                // e.g. to avoid fetching the conn string from storage
                var deviceService = this.factory.Resolve<IDevices>();
                await deviceService.InitAsync();

                if (await this.simulations.TryToStartDevicesCreationAsync(simulation.Id, deviceService))
                {
                    this.log.Info("Device creation started");
                }
                else
                {
                    this.log.Warn("Failed to start device creation, will retry later");
                }
            }
        }

        private async Task CreatePartitionsAsync(IList<Simulation> activeSimulations)
        {
            if (activeSimulations.Count == 0) return;

            var simulationsToPartition = activeSimulations.Where(x => x.PartitioningRequired).ToList();

            if (simulationsToPartition.Count == 0)
            {
                this.log.Debug("No simulations to be partitioned");
                return;
            }

            foreach (Simulation sim in simulationsToPartition)
            {
                await this.partitions.CreateAsync(sim.Id);
            }
        }

        private async Task DeletePartitionsAsync(IList<Simulation> activeSimulations)
        {
            if (activeSimulations.Count == 0) return;

            this.log.Debug("Searching partitions to delete...");

            var allPartitions = await this.partitions.GetAllAsync();
            var simulationIds = new HashSet<string>(activeSimulations.Select(x => x.Id));
            var partitionIds = new List<string>();
            foreach (var partition in allPartitions)
            {
                if (!simulationIds.Contains(partition.SimulationId))
                {
                    partitionIds.Add(partition.Id);
                }
            }

            if (partitionIds.Count == 0)
            {
                this.log.Debug("No partitions to delete");
                return;
            }

            // TODO: partitions should be deleted only after its actors are down
            this.log.Debug("Deleting partitions...", () => new { partitionIds.Count });
            await this.partitions.DeleteListAsync(partitionIds);
        }
    }
}
