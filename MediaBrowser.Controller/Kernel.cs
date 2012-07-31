﻿using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using MediaBrowser.Common.Json;
using MediaBrowser.Common.Kernel;
using MediaBrowser.Controller.Configuration;
using MediaBrowser.Controller.Events;
using MediaBrowser.Controller.IO;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Resolvers;
using MediaBrowser.Model.Configuration;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Users;

namespace MediaBrowser.Controller
{
    public class Kernel : BaseKernel<ServerConfiguration>
    {
        public static Kernel Instance { get; private set; }

        public ItemController ItemController { get; private set; }

        public IEnumerable<User> Users { get; private set; }
        public Folder RootFolder { get; private set; }

        private DirectoryWatchers DirectoryWatchers { get; set; }

        private string MediaRootFolderPath
        {
            get
            {
                return Path.Combine(ProgramDataPath, "Root");
            }
        }

        private string UsersPath
        {
            get
            {
                return Path.Combine(ProgramDataPath, "Users");
            }
        }
        
        /// <summary>
        /// Gets the list of currently registered entity resolvers
        /// </summary>
        [ImportMany(typeof(IBaseItemResolver))]
        public IEnumerable<IBaseItemResolver> EntityResolvers { get; private set; }
        
        /// <summary>
        /// Creates a kernal based on a Data path, which is akin to our current programdata path
        /// </summary>
        public Kernel()
            : base()
        {
            Instance = this;

            ItemController = new ItemController();
            DirectoryWatchers = new DirectoryWatchers();

            ItemController.PreBeginResolvePath += ItemController_PreBeginResolvePath;
            ItemController.BeginResolvePath += ItemController_BeginResolvePath;
        }

        protected override void OnComposablePartsLoaded()
        {
            List<IBaseItemResolver> resolvers = EntityResolvers.ToList();

            // Add the internal resolvers
            resolvers.Add(new VideoResolver());
            resolvers.Add(new AudioResolver());
            resolvers.Add(new FolderResolver());

            EntityResolvers = resolvers;

            // The base class will start up all the plugins
            base.OnComposablePartsLoaded();

            // Get users from users folder
            // Load root media folder
            Parallel.Invoke(ReloadUsers, ReloadRoot);
        }

        /// <summary>
        /// Fires when a path is about to be resolved, but before child folders and files 
        /// have been collected from the file system.
        /// This gives us a chance to cancel it if needed, resulting in the path being ignored
        /// </summary>
        void ItemController_PreBeginResolvePath(object sender, PreBeginResolveEventArgs e)
        {
            if (e.IsHidden || e.IsSystemFile)
            {
                // Ignore hidden files and folders
                e.Cancel = true;
            }

            else if (Path.GetFileName(e.Path).Equals("trailers", StringComparison.OrdinalIgnoreCase))
            {
                // Ignore any folders named "trailers"
                e.Cancel = true;
            }
        }

        /// <summary>
        /// Fires when a path is about to be resolved, but after child folders and files 
        /// This gives us a chance to cancel it if needed, resulting in the path being ignored
        /// </summary>
        void ItemController_BeginResolvePath(object sender, ItemResolveEventArgs e)
        {
            if (e.IsFolder)
            {
                if (e.ContainsFile(".ignore"))
                {
                    // Ignore any folders containing a file called .ignore
                    e.Cancel = true;
                }
            }
        }

        private void ReloadUsers()
        {
            Users = GetAllUsers();
        }

        /// <summary>
        /// Reloads the root media folder
        /// </summary>
        public void ReloadRoot()
        {
            if (!Directory.Exists(MediaRootFolderPath))
            {
                Directory.CreateDirectory(MediaRootFolderPath);
            }

            DirectoryWatchers.Stop();

            RootFolder = ItemController.GetItem(MediaRootFolderPath) as Folder;

            DirectoryWatchers.Start();
        }

        private static MD5CryptoServiceProvider md5Provider = new MD5CryptoServiceProvider();
        public static Guid GetMD5(string str)
        {
            lock (md5Provider)
            {
                return new Guid(md5Provider.ComputeHash(Encoding.Unicode.GetBytes(str)));
            }
        }

        public UserConfiguration GetUserConfiguration(Guid userId)
        {
            return Configuration.DefaultUserConfiguration;
        }
        
        public void ReloadItem(BaseItem item)
        {
            Folder folder = item as Folder;

            if (folder != null && folder.IsRoot)
            {
                ReloadRoot();
            }
            else
            {
                if (!Directory.Exists(item.Path) && !File.Exists(item.Path))
                {
                    ReloadItem(item.Parent);
                    return;
                }

                BaseItem newItem = ItemController.GetItem(item.Parent, item.Path);

                List<BaseItem> children = item.Parent.Children.ToList();

                int index = children.IndexOf(item);

                children.RemoveAt(index);

                children.Insert(index, newItem);

                item.Parent.Children = children.ToArray();
            }
        }

        /// <summary>
        /// Finds a library item by Id
        /// </summary>
        public BaseItem GetItemById(Guid id)
        {
            if (id == Guid.Empty)
            {
                return RootFolder;
            }

            return RootFolder.FindById(id);
        }

        /// <summary>
        /// Determines if an item is allowed for a given user
        /// </summary>
        public bool IsParentalAllowed(BaseItem item, Guid userId)
        {
            // not yet implemented
            return true;
        }

        /// <summary>
        /// Gets allowed children of an item
        /// </summary>
        public IEnumerable<BaseItem> GetParentalAllowedChildren(Folder folder, Guid userId)
        {
            return folder.Children.Where(i => IsParentalAllowed(i, userId));
        }

        /// <summary>
        /// Gets allowed recursive children of an item
        /// </summary>
        public IEnumerable<BaseItem> GetParentalAllowedRecursiveChildren(Folder folder, Guid userId)
        {
            foreach (var item in GetParentalAllowedChildren(folder, userId))
            {
                yield return item;

                var subFolder = item as Folder;

                if (subFolder != null)
                {
                    foreach (var subitem in GetParentalAllowedRecursiveChildren(subFolder, userId))
                    {
                        yield return subitem;
                    }
                }
            }
        }

        /// <summary>
        /// Gets user data for an item, if there is any
        /// </summary>
        public UserItemData GetUserItemData(Guid userId, Guid itemId)
        {
            User user = Users.First(u => u.Id == userId);

            if (user.ItemData.ContainsKey(itemId))
            {
                return user.ItemData[itemId];
            }

            return null;
        }

        /// <summary>
        /// Gets all recently added items (recursive) within a folder, based on configuration and parental settings
        /// </summary>
        public IEnumerable<BaseItem> GetRecentlyAddedItems(Folder parent, Guid userId)
        {
            DateTime now = DateTime.Now;

            UserConfiguration config = GetUserConfiguration(userId);

            return GetParentalAllowedRecursiveChildren(parent, userId).Where(i => !(i is Folder) && (now - i.DateCreated).TotalDays < config.RecentItemDays);
        }

        /// <summary>
        /// Gets all recently added unplayed items (recursive) within a folder, based on configuration and parental settings
        /// </summary>
        public IEnumerable<BaseItem> GetRecentlyAddedUnplayedItems(Folder parent, Guid userId)
        {
            return GetRecentlyAddedItems(parent, userId).Where(i =>
            {
                var userdata = GetUserItemData(userId, i.Id);

                return userdata == null || userdata.PlayCount == 0;
            });
        }

        /// <summary>
        /// Gets all in-progress items (recursive) within a folder
        /// </summary>
        public IEnumerable<BaseItem> GetInProgressItems(Folder parent, Guid userId)
        {
            return GetParentalAllowedRecursiveChildren(parent, userId).Where(i =>
            {
                if (i is Folder)
                {
                    return false;
                }

                var userdata = GetUserItemData(userId, i.Id);

                return userdata != null && userdata.PlaybackPosition.Ticks > 0;
            });
        }

        /// <summary>
        /// Finds all recursive items within a top-level parent that contain the given studio and are allowed for the current user
        /// </summary>
        public IEnumerable<BaseItem> GetItemsWithStudio(Folder parent, string studio, Guid userId)
        {
            return GetParentalAllowedRecursiveChildren(parent, userId).Where(f => f.Studios != null && f.Studios.Any(s => s.Equals(studio, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Finds all recursive items within a top-level parent that contain the given genre and are allowed for the current user
        /// </summary>
        public IEnumerable<BaseItem> GetItemsWithGenre(Folder parent, string genre, Guid userId)
        {
            return GetParentalAllowedRecursiveChildren(parent, userId).Where(f => f.Genres != null && f.Genres.Any(s => s.Equals(genre, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Finds all recursive items within a top-level parent that contain the given person and are allowed for the current user
        /// </summary>
        public IEnumerable<BaseItem> GetItemsWithPerson(Folder parent, string personName, Guid userId)
        {
            return GetParentalAllowedRecursiveChildren(parent, userId).Where(f => f.People != null && f.People.Any(s => s.Name.Equals(personName, StringComparison.OrdinalIgnoreCase)));
        }

        /// <summary>
        /// Gets all studios from all recursive children of a folder
        /// The CategoryInfo class is used to keep track of the number of times each studio appears
        /// </summary>
        public IEnumerable<CategoryInfo<Studio>> GetAllStudios(Folder parent, Guid userId)
        {
            Dictionary<string, int> data = new Dictionary<string, int>();

            // Get all the allowed recursive children
            IEnumerable<BaseItem> allItems = Kernel.Instance.GetParentalAllowedRecursiveChildren(parent, userId);

            foreach (var item in allItems)
            {
                // Add each studio from the item to the data dictionary
                // If the studio already exists, increment the count
                if (item.Studios == null)
                {
                    continue;
                }

                foreach (string val in item.Studios)
                {
                    if (!data.ContainsKey(val))
                    {
                        data.Add(val, 1);
                    }
                    else
                    {
                        data[val]++;
                    }
                }
            }

            // Now go through the dictionary and create a Category for each studio
            List<CategoryInfo<Studio>> list = new List<CategoryInfo<Studio>>();

            foreach (string key in data.Keys)
            {
                // Get the original entity so that we can also supply the PrimaryImagePath
                Studio entity = Kernel.Instance.ItemController.GetStudio(key);

                if (entity != null)
                {
                    list.Add(new CategoryInfo<Studio>()
                    {
                        Item = entity,
                        ItemCount = data[key]
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// Gets all genres from all recursive children of a folder
        /// The CategoryInfo class is used to keep track of the number of times each genres appears
        /// </summary>
        public IEnumerable<CategoryInfo<Genre>> GetAllGenres(Folder parent, Guid userId)
        {
            Dictionary<string, int> data = new Dictionary<string, int>();

            // Get all the allowed recursive children
            IEnumerable<BaseItem> allItems = Kernel.Instance.GetParentalAllowedRecursiveChildren(parent, userId);

            foreach (var item in allItems)
            {
                // Add each genre from the item to the data dictionary
                // If the genre already exists, increment the count
                if (item.Genres == null)
                {
                    continue;
                }

                foreach (string val in item.Genres)
                {
                    if (!data.ContainsKey(val))
                    {
                        data.Add(val, 1);
                    }
                    else
                    {
                        data[val]++;
                    }
                }
            }

            // Now go through the dictionary and create a Category for each genre
            List<CategoryInfo<Genre>> list = new List<CategoryInfo<Genre>>();

            foreach (string key in data.Keys)
            {
                // Get the original entity so that we can also supply the PrimaryImagePath
                Genre entity = Kernel.Instance.ItemController.GetGenre(key);

                if (entity != null)
                {
                    list.Add(new CategoryInfo<Genre>()
                    {
                        Item = entity,
                        ItemCount = data[key]
                    });
                }
            }

            return list;
        }

        /// <summary>
        /// Gets all users within the system
        /// </summary>
        private IEnumerable<User> GetAllUsers()
        {
            if (!Directory.Exists(UsersPath))
            {
                Directory.CreateDirectory(UsersPath);
            }

            List<User> list = new List<User>();

            foreach (string folder in Directory.GetDirectories(UsersPath, "*", SearchOption.TopDirectoryOnly))
            {
                User item = GetFromDirectory(folder);

                if (item != null)
                {
                    list.Add(item);
                }
            }

            return list;
        }

        /// <summary>
        /// Gets a User from it's directory
        /// </summary>
        private User GetFromDirectory(string path)
        {
            string file = Path.Combine(path, "user.js");

            return JsonSerializer.DeserializeFromFile<User>(file);
        }

        /// <summary>
        /// Creates a User with a given name
        /// </summary>
        public User CreateUser(string name)
        {
            var now = DateTime.Now;

            User user = new User()
            {
                Name = name,
                Id = Guid.NewGuid(),
                DateCreated = now,
                DateModified = now
            };

            user.Path = Path.Combine(UsersPath, user.Id.ToString());

            Directory.CreateDirectory(user.Path);

            JsonSerializer.SerializeToFile(user, Path.Combine(user.Path, "user.js"));

            return user;
        }

    }
}
