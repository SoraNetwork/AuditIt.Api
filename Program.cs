using System.Text;
using System.Text.Json.Serialization;
using AuditIt.Api.Data;
using AuditIt.Api.Models;
using AuditIt.Api.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.FileProviders;

var builder = WebApplication.CreateBuilder(args);

builder.WebHost.UseUrls("http://0.0.0.0:5990");

// Add services to the container.
builder.Services.AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
    });

builder.Services.AddDbContext<ApplicationDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"],
            ValidAudience = builder.Configuration["Jwt:Audience"],
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]))
        };
    });

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll",
        policyBuilder =>
        {
            policyBuilder.AllowAnyOrigin()
                   .AllowAnyMethod()
                   .AllowAnyHeader();
        });
});

// 配置钉钉服务

builder.Services.Configure<DingTalkConfiguration>(builder.Configuration.GetSection("DingTalk"));

builder.Services.AddMemoryCache();

builder.Services.AddHttpClient<IDingTalkService, DingTalkService>();
builder.Services.Configure<SfExpressOptions>(builder.Configuration.GetSection("SfExpress"));
builder.Services.AddHttpClient<ISfExpressService, SfExpressService>();
builder.Services.AddHttpClient<ISfDeliveryEstimateService, SfDeliveryEstimateService>();



// 注册快捷备注服务

builder.Services.AddScoped<IQuickRemarkService, QuickRemarkService>();

// 租赁平台相关服务
builder.Services.Configure<ReminderOptions>(builder.Configuration.GetSection("Reminders"));
builder.Services.Configure<AliyunNotificationOptions>(builder.Configuration.GetSection("AliyunNotification"));
builder.Services.AddScoped<IRenterService, RenterService>();
builder.Services.AddScoped<IRentalService, RentalService>();
builder.Services.AddScoped<ISettlementService, SettlementService>();
builder.Services.AddScoped<IItemListingService, ItemListingService>();
builder.Services.AddScoped<IReminderService, ReminderService>();
builder.Services.AddScoped<IAliyunShipmentReminderSender, AliyunShipmentReminderSender>();
builder.Services.AddScoped<IShipmentReminderService, ShipmentReminderService>();
builder.Services.AddScoped<INotificationChannel, InAppNotificationChannel>();
builder.Services.AddScoped<INotificationChannel, DingTalkNotificationChannel>();
builder.Services.AddHostedService<ReminderSweeper>();
builder.Services.AddHostedService<SfExpressRouteSyncWorker>();

// 用户/角色/权限服务
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection("Auth"));
builder.Services.Configure<DingTalkDirectorySyncOptions>(builder.Configuration.GetSection("DingTalkDirectorySync"));
builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddScoped<IRoleService, RoleService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddHostedService<DingTalkDirectorySyncWorker>();


builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

// 自动应用数据库迁移
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        var context = services.GetRequiredService<ApplicationDbContext>();
        context.Database.Migrate();
        await RbacSeeder.SeedAsync(context);
    }
    catch (Exception ex)
    {
        var logger = services.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
    }
}

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseStaticFiles(); // Enable serving static files from wwwroot

// Serve photos from the 'photos' directory
// Serve photos from the 'photos' directory in the content root
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(
        Path.Combine(builder.Environment.ContentRootPath, "photos")),
    RequestPath = "/photos"
});


app.UseCors("AllowAll");

app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();

app.Run();
