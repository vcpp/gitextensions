﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using DeleteUnusedBranches.Properties;
using GitUIPluginInterfaces;
using System.Text.RegularExpressions;

namespace DeleteUnusedBranches
{
    public sealed partial class DeleteUnusedBranchesForm : Form
    {
        private readonly SortableBranchesList branches = new SortableBranchesList();
        private int days;
        private string referenceBranch;
        private readonly IGitModule gitCommands;
        private readonly IGitUICommands _gitUICommands;
        private readonly IGitPlugin _gitPlugin;
        private CancellationTokenSource refreshCancellation;

        public DeleteUnusedBranchesForm(int days, string referenceBranch, IGitModule gitCommands, IGitUICommands gitUICommands, IGitPlugin gitPlugin)
        {
            InitializeComponent();

            this.referenceBranch = referenceBranch;
            this.days = days;
            this.gitCommands = gitCommands;
            _gitUICommands = gitUICommands;
            _gitPlugin = gitPlugin;
            imgLoading.Image = IsMonoRuntime() ? Resources.loadingpanel_static : Resources.loadingpanel;
            RefreshObsoleteBranches();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);

            mergedIntoBranch.Text = referenceBranch;
            olderThanDays.Value = days;

            BranchesGrid.DataSource = branches;
            ClearResults();
        }

        private static ICollection<Branch> GetObsoleteBranches(RefreshContext context)
        {
            return GetObsoleteBranchNames(context)
                .AsParallel()
                .WithCancellation(context.CancellationToken)
                .Select(branchName => LoadBranch(context, branchName))
                .ToList();
        }

        private static Branch LoadBranch(RefreshContext context, string branchName)
        {
            var commitLog = context.Commands.RunGitCmd(string.Concat("log --pretty=%ci\n%an\n%s ", branchName, "^1..", branchName)).Split('\n');
            DateTime commitDate;
            DateTime.TryParse(commitLog[0], out commitDate);
            var authorName = commitLog.Length > 1 ? commitLog[1] : string.Empty;
            var message = commitLog.Length > 2 ? commitLog[2] : string.Empty;

            return new Branch(branchName, commitDate, authorName, message, commitDate < DateTime.Now - context.ObsolescenceDuration);
        }

        private static IEnumerable<string> GetObsoleteBranchNames(RefreshContext context)
        {
            var regex = string.IsNullOrEmpty(context.RegexFilter) ? null : new Regex(context.RegexFilter, RegexOptions.Compiled);

            // TODO: skip current branch
            return GetBranchNames(context)
                .Where(branchName => !string.IsNullOrEmpty(branchName))
                .Select(branchName => branchName.Trim('*', ' ', '\n', '\r'))
                .Where(branchName => branchName != "HEAD" &&
                                     branchName != context.ReferenceBranch &&
                                     (!context.IncludeRemotes || branchName.StartsWith(context.RemoteRepositoryName + "/")) &&
                                     (regex == null || regex.IsMatch(branchName)));
        }

        private static IEnumerable<string> GetBranchNames(RefreshContext context)
        {
            var remotesFlag = context.IncludeRemotes ? " -r" : string.Empty;
            Func<string, string[]> getBranchNames = mergeFlag => context.Commands.RunGitCmd("branch" + remotesFlag + mergeFlag).Split('\n');

            switch (context.MergeFilter)
            {
                case MergeRelation.All:
                    return getBranchNames(string.Empty);
                case MergeRelation.MergedOnly:
                    return getBranchNames(" --merged");
                case MergeRelation.NothingToMerge:
                    var mergedBranches = getBranchNames(" --merged");
                    var unmergedBranches = getBranchNames(" --no-merged");
                    var emptyUnmergedBranches = unmergedBranches
                        .AsParallel()
                        .Where(branch => CalculateTentativeMergeDiff(branch, context.ReferenceBranch, context.Commands).Length == 0);
                    return mergedBranches.Concat(emptyUnmergedBranches);
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }

        /// <summary>
        /// Returns result of virtual three-way merge of the specified branches.
        /// Empty result means that <see cref="mergeFromBranchName"/> doesn't contain changes to be merged into <see cref="mergeToBranchName"/>.
        /// </summary>
        private static string CalculateTentativeMergeDiff(string mergeFromBranchName, string mergeToBranchName, IGitModule git)
        {
            var mergeBase = git.RunGitCmd(string.Format("merge-base {0} {1}", mergeToBranchName, mergeFromBranchName)).Trim();
            return git.RunGitCmd(string.Format("merge-tree {0} {1} {2}", mergeBase, mergeToBranchName, mergeFromBranchName)).Trim();
        }

        private void Delete_Click(object sender, EventArgs e)
        {
            var selectedBranches = branches.Where(branch => branch.Delete).ToList();
            if (selectedBranches.Count == 0)
            {
                MessageBox.Show(string.Format("Select branches to delete using checkboxes in '{0}' column.", deleteDataGridViewCheckBoxColumn.HeaderText), "Delete");
                return;
            }

            if (MessageBox.Show(this, string.Format("Are you sure to delete {0} selected branches?", selectedBranches.Count), "Delete", MessageBoxButtons.YesNo) != DialogResult.Yes)
                return;

            var remoteName = remote.Text;
            var remoteBranchPrefix = remoteName + "/";
            var remoteBranchesSource = IncludeRemoteBranches.Checked
                ? selectedBranches.Where(branch => branch.Name.StartsWith(remoteBranchPrefix))
                : Enumerable.Empty<Branch>();
            var remoteBranches = remoteBranchesSource.ToList();

            if (remoteBranches.Count > 0)
            {
                var message = string.Format("DANGEROUS ACTION!{0}Branches will be deleted on the remote '{1}'. This can not be undone.{0}Are you sure you want to continue?", Environment.NewLine, remoteName);
                if (MessageBox.Show(this, message, "Delete", MessageBoxButtons.YesNo) != DialogResult.Yes)
                    return;
            }

            var localBranches = selectedBranches.Except(remoteBranches).ToList();
            tableLayoutPanel2.Enabled = tableLayoutPanel3.Enabled = false;
            imgLoading.Visible = true;
            lblStatus.Text = "Deleting branches...";

            Task.Factory.StartNew(() =>
            {
                if (remoteBranches.Count > 0)
                {
                    // TODO: use GitCommandHelpers.PushMultipleCmd after moving this window to GE (see FormPush as example)
                    var remoteBranchNameOffset = remoteBranchPrefix.Length;
                    var remoteBranchNames = string.Join(" ", remoteBranches.Select(branch => ":" + branch.Name.Substring(remoteBranchNameOffset)));
                    gitCommands.RunGitCmd(string.Format("push {0} {1}", remoteName, remoteBranchNames));
                }

                if (localBranches.Count > 0)
                {
                    var localBranchNames = string.Join(" ", localBranches.Select(branch => branch.Name));
                    gitCommands.RunGitCmd("branch -d " + localBranchNames);
                }
            })
            .ContinueWith(_ =>
            {
                if (IsDisposed)
                    return;

                tableLayoutPanel2.Enabled = tableLayoutPanel3.Enabled = true;
                RefreshObsoleteBranches();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void buttonSettings_Click(object sender, EventArgs e)
        {
            Hide();
            Close();
            _gitUICommands.StartSettingsDialog(_gitPlugin);
        }

        private void IncludeRemoteBranches_CheckedChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void useRegexFilter_CheckedChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void remote_TextChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void regexFilter_TextChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void mergedIntoBranch_TextChanged(object sender, EventArgs e)
        {
            referenceBranch = mergedIntoBranch.Text;
            ClearResults();
        }

        private void includeUnmergedBranches_CheckedChanged(object sender, EventArgs e)
        {
            ClearResults();

            if (includeUnmergedBranches.Checked)
                MessageBox.Show(this, "Deleting unmerged branches will result in dangling commits. Use with caution!", "Delete", MessageBoxButtons.OK);
            pnlUnmergedFilterOptions.Enabled = includeUnmergedBranches.Checked;
        }

        private void olderThanDays_ValueChanged(object sender, EventArgs e)
        {
            days = (int)olderThanDays.Value;
            ClearResults();
        }

        private void optEmptyMergeBranches_CheckedChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void optAllBranches_CheckedChanged(object sender, EventArgs e)
        {
            ClearResults();
        }

        private void ClearResults()
        {
            instructionLabel.Text = "Choose branches to delete. Only branches that are fully merged in '" + referenceBranch + "' will be deleted.";
            lblStatus.Text = "Press '" + RefreshBtn.Text + "' to search for branches to delete.";
            branches.Clear();
            branches.ResetBindings();
        }

        private void Refresh_Click(object sender, EventArgs e)
        {
            RefreshObsoleteBranches();
        }

        private void BranchesGrid_CellContentClick(object sender, DataGridViewCellEventArgs e)
        {
            // track only “Deleted” column
            if (e.ColumnIndex != 0)
                return;

            BranchesGrid.CommitEdit(DataGridViewDataErrorContexts.Commit);
            lblStatus.Text = GetDefaultStatusText();
        }

        private void RefreshObsoleteBranches()
        {
            if (IsRefreshing)
            {
                refreshCancellation.Cancel();
                IsRefreshing = false;
                return;
            }

            IsRefreshing = true;

            var mergeFilter = includeUnmergedBranches.Checked
                ? optAllBranches.Checked ? MergeRelation.All : MergeRelation.NothingToMerge
                : MergeRelation.MergedOnly;
            var context = new RefreshContext(gitCommands, IncludeRemoteBranches.Checked, mergeFilter, referenceBranch, remote.Text,
                useRegexFilter.Checked ? regexFilter.Text : null, TimeSpan.FromDays(days), refreshCancellation.Token);
            Task.Factory.StartNew(() => GetObsoleteBranches(context), CancellationToken.None, TaskCreationOptions.None, TaskScheduler.Default)
                .ContinueWith(task =>
                {
                    if (IsDisposed || context.CancellationToken.IsCancellationRequested)
                        return;

                    if (task.IsCompleted)
                    {
                        branches.Clear();
                        branches.AddRange(task.Result);
                        branches.ResetBindings();
                    }

                    IsRefreshing = false;
                }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private bool IsRefreshing
        {
            get
            {
                return refreshCancellation != null;
            }
            set
            {
                if (value == IsRefreshing)
                    return;

                refreshCancellation = value ? new CancellationTokenSource() : null;
                RefreshBtn.Text = value ? "Cancel" : "Search branches";
                imgLoading.Visible = value;
                lblStatus.Text = value ? "Loading..." : GetDefaultStatusText();
            }
        }

        private string GetDefaultStatusText()
        {
            return string.Format("{0}/{1} branches selected.", branches.Count(b => b.Delete), branches.Count);
        }

        private static bool IsMonoRuntime()
        {
            return Type.GetType("Mono.Runtime") != null;
        }

        private struct RefreshContext
        {
            private readonly IGitModule commands;
            private readonly bool includeRemotes;
            private readonly MergeRelation mergeFilter;
            private readonly string referenceBranch;
            private readonly string remoteRepositoryName;
            private readonly string regexFilter;
            private readonly TimeSpan obsolescenceDuration;
            private readonly CancellationToken cancellationToken;

            public RefreshContext(IGitModule commands, bool includeRemotes, MergeRelation mergeFilter, string referenceBranch,
                string remoteRepositoryName, string regexFilter, TimeSpan obsolescenceDuration, CancellationToken cancellationToken)
            {
                this.commands = commands;
                this.includeRemotes = includeRemotes;
                this.mergeFilter = mergeFilter;
                this.referenceBranch = referenceBranch;
                this.remoteRepositoryName = remoteRepositoryName;
                this.regexFilter = regexFilter;
                this.obsolescenceDuration = obsolescenceDuration;
                this.cancellationToken = cancellationToken;
            }

            public IGitModule Commands
            {
                get { return commands; }
            }

            public bool IncludeRemotes
            {
                get { return includeRemotes; }
            }

            public MergeRelation MergeFilter
            {
                get { return mergeFilter; }
            }

            public string ReferenceBranch
            {
                get { return referenceBranch; }
            }

            public string RemoteRepositoryName
            {
                get { return remoteRepositoryName; }
            }

            public string RegexFilter
            {
                get { return regexFilter; }
            }

            public TimeSpan ObsolescenceDuration
            {
                get { return obsolescenceDuration; }
            }

            public CancellationToken CancellationToken
            {
                get { return cancellationToken; }
            }
        }

        private enum MergeRelation
        {
            /// <summary>
            /// Both merged and unmerged branches.
            /// </summary>
            All,

            /// <summary>
            /// Only merged branches.
            /// </summary>
            MergedOnly,

            /// <summary>
            /// Branches where nothing to merge into selected root branch: already merged or unmerged with changes which are also exist in root branch (cherry-picked, for example)-.
            /// </summary>
            NothingToMerge
        }
    }
}
