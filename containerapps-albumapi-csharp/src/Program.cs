using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Identity;
using Azure.Security.KeyVault.Secrets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;


namespace key_vault_console_app
{
    class Program
    {
        static async Task Main(string[] args)
        {

            var builder = WebApplication.CreateBuilder();

            // Add services to the container.
            // Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
            builder.Services.AddEndpointsApiExplorer();
            builder.Services.AddSwaggerGen();

            builder.Services.AddCors(options => {
                options.AddDefaultPolicy(builder =>
                {
                    builder.AllowAnyOrigin();
                    builder.AllowAnyHeader();
                    builder.AllowAnyMethod();
                });
            });

            var app = builder.Build();

            // Configure the HTTP request pipeline.
            if (app.Environment.IsDevelopment())
            {
                app.UseSwagger();
                app.UseSwaggerUI();
            }

            app.UseCors();

            app.MapGet("/", async context =>
            {
                await context.Response.WriteAsync("Hit the /albums endpoint to retrieve a list of albums!");
            });

            app.MapGet("/albums", () =>
            {
                return Album.GetAll();
            })
            .WithName("GetAlbums");

            _ = app.MapGet("/secrets", async () =>
            {
                var KeyVaultUrl = app.Configuration.GetValue<string>("KEY_VAULT_URL")
                    ?? throw new InvalidOperationException("KEY_VAULT_URL environment variable is not set");

                var AzureKeyVaultClient = new SecretClient(
                    new Uri(KeyVaultUrl),
                    new DefaultAzureCredential()
                );

                // Requires the VM (or other platform Managed Identity - like the Container App Managed Identity),
                // to get the assigned RBAC role "Key Vault Reader" on the target key vault.
                var allSecrets = AzureKeyVaultClient.GetPropertiesOfSecretsAsync()
                    ?? throw new InvalidOperationException("No secrets found in the Key Vault.");

                var secretsList = new List<string>();
                await foreach (SecretProperties secretProperties in allSecrets)
                {
                    secretsList.Add(secretProperties.Name);
                }

                return secretsList;
            })
            .WithName("GetSecrets");

            await app.RunAsync();
        }

        record Album(int Id, string Title, string Artist, double Price, string Image_url)
        {
            public static List<Album> GetAll() {
                var albums = new List<Album>(){
            // new Album(0, "Emmanuel test", "On Sunday", 111, "https://www.microsoft.com"),
            // new Album(62, "Emmanuel test 2", "On Monday evening", 2222, "https://www.microsoft.com"),

            new Album(1, "You, Me and an App Id", "Daprize", 10.99, "https://aka.ms/albums-daprlogo"),
            new Album(2, "Seven Revision Army", "The Blue-Green Stripes", 13.99, "https://aka.ms/albums-containerappslogo"),
            new Album(3, "Scale It Up", "KEDA Club", 13.99, "https://aka.ms/albums-kedalogo"),
            new Album(4, "Lost in Translation", "MegaDNS", 12.99,"https://aka.ms/albums-envoylogo"),
            new Album(5, "Lock Down Your Love", "V is for VNET", 12.99, "https://aka.ms/albums-vnetlogo"),
            new Album(6, "Sweet Container O' Mine", "Guns N Probeses", 14.99, "https://aka.ms/albums-containerappslogo")
         };

                return albums;
            }
        }
    }
}