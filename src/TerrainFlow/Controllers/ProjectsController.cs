﻿using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using GeoTiffSharp;
using Microsoft.AspNet.Authorization;
using Microsoft.AspNet.Hosting;
using Microsoft.AspNet.Http;
using Microsoft.AspNet.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Net.Http.Headers;
using TerrainFlow.ViewModels.Projects;

namespace TerrainFlow.Controllers
{
    [Authorize]
    public class ProjectsController : Controller
    {
        private IConfiguration _configuration;
        private readonly IHostingEnvironment _environment;
        private Storage _storage;

        public ProjectsController(IHostingEnvironment environment, IConfiguration configuration)
        {
            _storage = new Storage(configuration);

            _environment = environment;
            _configuration = configuration;
        }

        private ICollection<ProjectViewModel> GetProjectsFromTables()
        {
            var projects = _storage.GetProjectsForUser(GetEmailFromUser());

            var collection = new Collection<ProjectViewModel>();

            foreach (var entity in projects)
            {
                collection.Add(new ProjectViewModel
                {
                    Name = entity.Name,
                    URL = entity.RowKey
                });
            }

            return collection;
        }

        [HttpPost]
        public async Task<IActionResult> UploadProjectFiles()
        {
            var files = Request.Form.Files;
            var hashes = new List<string>();

            Trace.TraceInformation("Upload called with {0} files", files.Count());

            try
            {
                foreach (var file in files.Where(file => file.Length > 0))
                {
                    hashes.Add(await ProcessUpload(file));
                }

                return new JsonResult(hashes);
            }
            catch (Exception ex)
            {
                Trace.TraceError("Failed to process upload. \n" + ex.ToString());
                return new HttpStatusCodeResult(StatusCodes.Status400BadRequest);
            }
        }

        // /Projects/
        [HttpGet]
        public IActionResult Index()
        {
            var projects = new ProjectsViewModel
            {
                Projects = GetProjectsFromTables()
            };

            return View(projects);
        }

        // /Projects/Add
        [HttpGet]
        public IActionResult Add()
        {
            if (GetEmailFromUser() == null)
            {
                return RedirectToAction("Signin", "Account");
            }

            return View();
        }

        #region Helpers

        private async Task<string> ProcessUpload(IFormFile file)
        {
            Trace.TraceInformation("Process upload for {0}", file.ToString());

            var t = DateTime.UtcNow - new DateTime(1970, 1, 1);
            var epoch = (int)t.TotalSeconds;
            var sourceName = ContentDispositionHeaderValue.Parse(file.ContentDisposition).FileName.Trim('"');
            var filePath = Path.GetTempFileName();

            // Save locally
            await file.SaveAsAsync(filePath);

            var resultPaths = ConvertFiles(filePath, sourceName);

            if (resultPaths != null && resultPaths.Any())
            {
                foreach (var path in resultPaths)
                {
                    Trace.TraceInformation("Moving to blog store: {0}", path);
                    await _storage.UploadFileToBlob(path, Path.GetFileName(path));
                }

                _storage.SaveFileToTables(sourceName, Path.GetFileNameWithoutExtension(resultPaths.First()), GetEmailFromUser());
            }


            return sourceName;
        }

        // Rough implementation, support for zipped tiff's
        private IEnumerable<string> ConvertFiles(string filePath, string sourceName)
        {
            string workingDirectory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);

            // If ZIP, extract first
            if (string.Equals(Path.GetExtension(sourceName), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                Trace.TraceInformation("Found zip file, decompressing.");

                ZipFile.ExtractToDirectory(filePath, workingDirectory);

                var files = Directory.GetFiles(workingDirectory);

                // Lets see if we have a tiff file for now
                var tiff = files.Where(f => string.Equals(Path.GetExtension(f), ".tif", StringComparison.OrdinalIgnoreCase)).FirstOrDefault();

                if (!string.IsNullOrEmpty(tiff))
                {
                    Trace.TraceInformation("Found tiff, converting.");

                    var GeoTiff = new GeoTiff();
                    var outputRoot = Path.GetTempFileName();

                    var outputBinary = outputRoot + ".dat";
                    var outputMetadata = outputRoot + ".json";
                    var outputThumbnail = outputRoot + ".png";

                    GeoTiff.ConvertToHeightMap(tiff, outputBinary, outputMetadata, outputThumbnail);

                    return new List<string> { outputBinary, outputMetadata, outputThumbnail };
                }
            }

            return null;
        }

        private string CreateHashFromFile(string filePath)
        {
            using (var md5 = MD5.Create())
            {
                using (var stream = System.IO.File.OpenRead(filePath))
                {
                    var hash = md5.ComputeHash(stream);
                    return string.Concat(hash.Select(x => x.ToString("X2")));
                }
            }
        }

        private string GetEmailFromUser()
        {
            var identity = (ClaimsIdentity)User.Identity;
            var email = identity.FindFirst(ClaimTypes.Email).Value;

            return email;
        }

        #endregion
    }
}