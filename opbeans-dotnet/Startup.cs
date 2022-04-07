using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpbeansDotnet.Data;
using OpbeansDotnet.Model;
using OpenTelemetry;
using OpenTelemetry.Trace;
using OpenTelemetry.Resources;
using OpenTelemetry.Instrumentation.AspNetCore;
using OpenTelemetry.Contrib.Instrumentation.EntityFrameworkCore;
using OpenTelemetry.Instrumentation.Http;
using OpenTelemetry.Exporter;
using OpenTelemetry.Extensions.Hosting;
using OpenTelemetry.Metrics;
using Elastic.Apm.NetCoreAll;

//using OpbeansDotnet.Data;

namespace OpbeansDotnet
{
	public class Startup
	{
		public Startup(IConfiguration configuration) => Configuration = configuration;

		public IConfiguration Configuration { get; }

		// This method gets called by the runtime. Use this method to add services to the container.
		public void ConfigureServices(IServiceCollection services)
		{
			Console.WriteLine(Environment.GetEnvironmentVariable("APM_AGENT_TYPE"));
			services.AddMvc();
			services.AddControllers();

			if(Environment.GetEnvironmentVariable("APM_AGENT_TYPE") == "opentelemetry"){
				services.AddHttpClient();
				AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);
				services.AddOpenTelemetryTracing((builder) => builder
					.SetResourceBuilder(ResourceBuilder.CreateDefault()
						.AddAttributes(new Dictionary<string, object>{
							["container.id"] = Utilities.GetContainerId()
						})
						.AddTelemetrySdk()
					)
					.AddAspNetCoreInstrumentation()
					.AddHttpClientInstrumentation()
					.AddOtlpExporter(opt =>
					{
						opt.Endpoint = new Uri(Environment.GetEnvironmentVariable("OTEL_EXPORTER_OTLP_ENDPOINT"));
						Console.WriteLine(opt.Endpoint);
						opt.Protocol = OtlpExportProtocol.Grpc;
					})
				);
			}

			var context = new OpbeansDbContext();
			context.Database.EnsureCreated();
			if (!context.Products.Any() || !context.Customers.Any() || !context.Orders.Any())
				Seed.SeedDb(context);

			services.AddDbContext<OpbeansDbContext>
				(options => options.UseSqlite(@"Data Source=opbeans.db"));
		}

		// This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
		public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
		{
			Console.WriteLine(Environment.GetEnvironmentVariable("APM_AGENT_TYPE"));
			if(Environment.GetEnvironmentVariable("APM_AGENT_TYPE") == "elasticapm"){
				app.UseAllElasticApm(Configuration);
			}

			// Read config environment variables used to demonstrate Distributed Tracing
			// For more info see: https://github.com/elastic/apm-integration-testing/issues/196
			app.Use(async (context, next) =>
			{
				if (context.Request.Path.HasValue && KnownApis.Contains(context.Request.Path.Value))
				{
					var opbeansServices = Environment.GetEnvironmentVariable("OPBEANS_SERVICES");
					if (!string.IsNullOrEmpty(opbeansServices))
					{
						var allServices = opbeansServices.Split(',')?.Select(n => n.ToLower())
							.Where(n => n != "opbeans-dotnet")
							.ToList();

						if (allServices != null && allServices.Any())
						{
							var dtProbabilityEnvVar = Environment.GetEnvironmentVariable("OPBEANS_DT_PROBABILITY");

							if (!double.TryParse(dtProbabilityEnvVar, NumberStyles.Float, CultureInfo.InvariantCulture,
								out var dtProbability))
								dtProbability = 0.5;

							var random = new Random(DateTime.UtcNow.Millisecond);

							if (random.NextDouble() > dtProbability)
							{
								await next.Invoke();
								return;
							}

							var winnerService = allServices[random.Next(allServices.Count)];

							if (!winnerService.StartsWith("http"))
								winnerService = $"http://{winnerService}";
							if (winnerService.EndsWith("/"))
								winnerService = winnerService.Substring(0, winnerService.Length - 1);

							var httpClient = new HttpClient();

							try
							{
								await httpClient.GetAsync(
									$"{winnerService}:{context.Request.Host.Port}/{context.Request.Path.Value}");
							}
							catch
							{
								//Ignore error, it'll be captured by the agent, but there is nothing to do.
							}
						}
					}
				}

				await next.Invoke();
			});

			Mapper.Initialize(cfg =>
			{
				cfg.CreateMap<Orders, Order>()
					.ForMember(dest => dest.CustomerId, opt => opt.MapFrom(src => src.Customer.Id))
					.ForMember(dest => dest.CustomerName, opt => opt.MapFrom(src => src.Customer.FullName));

				cfg.CreateMap<Products, Product>()
					.ForMember(dest => dest.Type_id, opt => opt.MapFrom(src => src.Type.Id))
					.ForMember(dest => dest.Type_name, opt => opt.MapFrom(src => src.Type.Name));
				cfg.CreateMap<ProductTypes, ProductType>();
			});

			app.UseDeveloperExceptionPage();

			app.UseHttpsRedirection();

			app.UseRouting();
			app.UseEndpoints(endpoints =>
			{
				endpoints.MapControllers();
			});

			if(Environment.GetEnvironmentVariable("APM_AGENT_TYPE") == "opentelemetry"){
				Thread metricsThread = new Thread(SystemMetrics.InitMetrics);
				metricsThread.Start();
			}

		}

		private static List<string> KnownApis =>
			new List<string>
			{
				"/api/",
				"/api/stats",
				"/api/products",
				"/api/products/",
				"/api/products/top",
				"/api/products/customers",
				"/api/types",
				"/api/types/",
				"/api/customers",
				"/api/customers/",
				"/api/orders",
				"/api/orders/"
			};
	}

	public class SystemMetrics {

	private static Meter meter;
	private static OtlpMetricExporter exporter;
	private static BaseExportingMetricReader reader;
	private static Counter<long> memoryUsageCounter;
	private static ObservableGauge<double> cpuUtilizationGauge;

	public static void InitMetrics()
	{

		meter = new Meter("OpbeansDotnetMetrics");
		exporter = new OtlpMetricExporter(new OtlpExporterOptions());
		reader = new BaseExportingMetricReader(exporter);

		memoryUsageCounter = meter.CreateCounter<long>("system.memory.usage");
		cpuUtilizationGauge = meter.CreateObservableGauge<double>("system.cpu.utilization", CollectCPUUtilization);

		using var meterProvider = Sdk.CreateMeterProviderBuilder()
			.SetResourceBuilder(ResourceBuilder.CreateDefault()
				.AddAttributes(new Dictionary<string, object>{
					["container.id"] = Utilities.GetContainerId()
				})
				.AddTelemetrySdk()
			)
			.AddMeter("OpbeansDotnetMetrics")
			.AddReader(reader)
			.Build();

		CollectMemoryUsage();
	}

	public static void CollectMemoryUsage(){
		while(true){
			long totalMem = Utilities.GetTotalMemory();
			long freeMem = Utilities.GetFreeMemory();
			long usedMem = totalMem - freeMem;
			memoryUsageCounter.Add(freeMem, new KeyValuePair<string, object>("state", "free"));
			memoryUsageCounter.Add(usedMem, new KeyValuePair<string, object>("state", "used"));
			reader.Collect();
			memoryUsageCounter.Add(-freeMem, new KeyValuePair<string, object>("state", "free"));
			memoryUsageCounter.Add(-usedMem, new KeyValuePair<string, object>("state", "used"));
			Thread.Sleep(500);
		}
	}

	public static IEnumerable<Measurement<double>> CollectCPUUtilization(){

		var startTime = DateTime.UtcNow;
		var startCpuUsage = new TimeSpan();
		foreach(Process p in Process.GetProcesses()){
			startCpuUsage += p.TotalProcessorTime;
		}
		var stopWatch = new Stopwatch();
		// Start watching CPU
		stopWatch.Start();

		// Meansure something else, such as .Net Core Middleware
		Thread.Sleep(500);

		// Stop watching to meansure
		stopWatch.Stop();
		var endTime = DateTime.UtcNow;
		var endCpuUsage = new TimeSpan();
		foreach(Process p in Process.GetProcesses()){
			endCpuUsage += p.TotalProcessorTime;
		}

		var cpuUsedMs = (endCpuUsage - startCpuUsage).TotalMilliseconds;
		var totalMsPassed = ((endTime - startTime).TotalMilliseconds) * Environment.ProcessorCount;
		var cpuUsageTotal = cpuUsedMs / totalMsPassed;
		//Console.WriteLine("CPU" + cpuUsageTotal.ToString());
		yield return new Measurement<double>(cpuUsageTotal, new KeyValuePair<string, object>("state", "active"), new KeyValuePair<string, object>("cpu", "0"));
	}

}

public class Utilities {

		public static long GetTotalMemory()
		{
				Process process = new Process();
				process.StartInfo.FileName = "/bin/bash";
				process.StartInfo.Arguments = "-c + \" "+ "grep MemTotal /proc/meminfo | awk '{print $2}'" + " \"";
				process.StartInfo.UseShellExecute = false;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;
				process.Start();
				//* Read the output (or the error)
				string output = process.StandardOutput.ReadToEnd();
				//Console.WriteLine(output);
				string err = process.StandardError.ReadToEnd();
			  //Console.WriteLine(err);
				process.WaitForExit();
				return long.Parse(output)*1024;
		}

		public static long GetFreeMemory()
		{
				Process process = new Process();
				process.StartInfo.FileName = "/bin/bash";
				process.StartInfo.Arguments = "-c + \" "+ "grep MemFree /proc/meminfo | awk '{print $2}'" + " \"";
				process.StartInfo.UseShellExecute = false;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;
				process.Start();
				//* Read the output (or the error)
				string output = process.StandardOutput.ReadToEnd();
				//Console.WriteLine(output);
				string err = process.StandardError.ReadToEnd();
				//Console.WriteLine(err);
				process.WaitForExit();
				return long.Parse(output)*1024;
		}

		public static string GetContainerId()
		{
				Process process = new Process();
				process.StartInfo.FileName = "/bin/bash";
				process.StartInfo.Arguments = "-c + \" "+ "basename $(cat /proc/1/cpuset)" + " \"";
				process.StartInfo.UseShellExecute = false;
				process.StartInfo.RedirectStandardOutput = true;
				process.StartInfo.RedirectStandardError = true;
				process.Start();
				//* Read the output (or the error)
				string output = process.StandardOutput.ReadToEnd();
				//Console.WriteLine(output);
				string err = process.StandardError.ReadToEnd();
				//Console.WriteLine(err);
				process.WaitForExit();
				return output;
		}

	}

}
