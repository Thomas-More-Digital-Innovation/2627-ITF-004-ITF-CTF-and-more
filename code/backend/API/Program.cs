var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
builder.Services.AddOpenApi();

// Lowercase urls
builder.Services.AddRouting(options => options.LowercaseUrls = true);

builder.Services.AddControllers();

builder.Services.AddScoped<Application.Interfaces.IEnvironmentService, Infrastructure.Services.EnvironmentService>();

// API documentation
builder.Services.AddOpenApi();
builder.Services.AddSwaggerGen();


// CORS
builder.Services.AddCors(options => options.AddDefaultPolicy(policy =>
{
    var section = builder.Configuration.GetSection("CorsSettings");
    var origins = section.GetSection("AllowedOrigins").Get<string[]>() ?? [];
    var methods = section.GetSection("AllowedMethods").Get<string[]>() ?? [];
    var headers = section.GetSection("AllowedHeaders").Get<string[]>() ?? [];
    var exposed = section.GetSection("ExposedHeaders").Get<string[]>() ?? [];

    _ = origins.Contains("*") ? policy.AllowAnyOrigin() : policy.WithOrigins(origins);
    _ = methods.Contains("*") ? policy.AllowAnyMethod() : policy.WithMethods(methods);
    _ = headers.Contains("*") ? policy.AllowAnyHeader() : policy.WithHeaders(headers);

    if (exposed.Length > 0) policy.WithExposedHeaders(exposed);
    if (section.GetValue<bool>("AllowCredentials") && !origins.Contains("*")) policy.AllowCredentials();
}));

var app = builder.Build();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// API documentation
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseHttpsRedirection();

app.UseCors();

app.MapControllers();

app.Run();